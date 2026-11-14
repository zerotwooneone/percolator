using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Percolator.Apps.Discovery.Features.PrivateRendezvous;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.Ports;
using Percolator.Infrastructure.Protos;
using Percolator.Infrastructure.Transport;
using Percolator.Infrastructure.Transport.Endpoints;
using Percolator.Simulator.Features.DirectChat;
using Percolator.Simulator.Features.Logging;
using Percolator.Simulator.Features.RichChatAndProfiles;
using Percolator.Simulator.Features.RelayedChat;
using Percolator.Simulator.Features.Shell;
using Percolator.Simulator.Features.Topology;
using PublicIdentityId = Percolator.Domain.Identities.PublicIdentityId;

namespace Percolator.Simulator.Features.DiscoverySimulation;

internal sealed class DiscoverySimulationCoordinator
{
    private readonly ISimulatedWorldStateService _worldState;
    private readonly ISimulatorStateBus _stateBus;
    private readonly ISimulatorLogSink _logSink;
    private readonly IStatusToastService _toastService;
    private readonly IVirtualClockStateService _virtualClock;
    private readonly DirectHandshakeOrchestrator _handshakeOrchestrator;
    private readonly RelayProvisioningCoordinator _relayCoordinator;

    private readonly ConcurrentDictionary<PublicIdentityId, List<BlindedRendezvousMatchProto>> _resolvedLocators = new();
    private readonly ConcurrentDictionary<PublicIdentityId, List<PeerDiscoveryDescriptorProto>> _lastDiscoveredPeers = new();

    public DiscoverySimulationCoordinator(
        ISimulatedWorldStateService worldState,
        ISimulatorStateBus stateBus,
        ISimulatorLogSink logSink,
        IStatusToastService toastService,
        IVirtualClockStateService virtualClock,
        DirectHandshakeOrchestrator handshakeOrchestrator,
        RelayProvisioningCoordinator relayCoordinator)
    {
        _worldState = worldState ?? throw new ArgumentNullException(nameof(worldState));
        _stateBus = stateBus ?? throw new ArgumentNullException(nameof(stateBus));
        _logSink = logSink ?? throw new ArgumentNullException(nameof(logSink));
        _toastService = toastService ?? throw new ArgumentNullException(nameof(toastService));
        _virtualClock = virtualClock ?? throw new ArgumentNullException(nameof(virtualClock));
        _handshakeOrchestrator = handshakeOrchestrator ?? throw new ArgumentNullException(nameof(handshakeOrchestrator));
        _relayCoordinator = relayCoordinator ?? throw new ArgumentNullException(nameof(relayCoordinator));
    }

    public async Task<bool> SetDiscoveryVisibilityAsync(
        ISimulatedPersonaHost persona,
        bool isDiscoverable,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(persona);

        persona.AllowPeerDiscovery = isDiscoverable;

        if (persona.HomeRelay != null)
        {
            try
            {
                using var scope = persona.Services.CreateScope();
                var channelFactory = scope.ServiceProvider.GetRequiredService<IPeerGrpcChannelFactory>();
                var cryptoEngine = scope.ServiceProvider.GetRequiredService<ICryptoEngine>();
                var relayUri = new Uri($"https://127.0.0.1:{persona.HomeRelay.BoundPort}");
                var channel = channelFactory.GetOrCreateChannel(relayUri);
                var client = new NearbyDiscoveryService.NearbyDiscoveryServiceClient(channel);

                int visibility = isDiscoverable ? 1 : 0;
                long timestampUnixMs = _virtualClock.UtcNow.ToUnixTimeMilliseconds();
                // Advertise the mailbox capability tokens only when discoverable; the
                // signature binds them so the relay cannot substitute its own mailbox.
                byte[]? routingToken = isDiscoverable ? persona.MailboxRoutingToken.ToByteArray() : null;
                byte[]? deliveryToken = isDiscoverable ? persona.MailboxDeliveryToken.Memory.ToArray() : null;
                var payload = DiscoveryWireFraming.BuildVisibilitySignaturePayload(
                    persona.PublicIdentityId, visibility, timestampUnixMs, routingToken, deliveryToken);
                var signature = cryptoEngine.SignEd25519(persona.IdentityPrivateKey.Span, payload);

                await client.RegisterVisibilityAsync(new DiscoveryRegistrationProto
                {
                    PeerId = ByteString.CopyFrom(persona.PublicIdentityId.ToByteArray()),
                    Visibility = visibility,
                    TimestampUnixMs = timestampUnixMs,
                    Signature = ByteString.CopyFrom(signature),
                    IdentityKey = ByteString.CopyFrom(persona.IdentityPublicKey.Span),
                    RoutingToken = routingToken != null ? ByteString.CopyFrom(routingToken) : ByteString.Empty,
                    DeliveryToken = deliveryToken != null ? ByteString.CopyFrom(deliveryToken) : ByteString.Empty
                }, cancellationToken: ct).ConfigureAwait(false);

                _logSink.Log(new SimulatorLogEntry(
                    _virtualClock.UtcNow,
                    LogLevel.Information,
                    persona.DisplayName,
                    persona.Id,
                    "Discovery",
                    $"Updated discovery visibility to {(isDiscoverable ? "Discoverable" : "Hidden")} on relay '{persona.HomeRelay.DisplayName}'."));
            }
            catch (Exception ex)
            {
                _logSink.Log(new SimulatorLogEntry(
                    _virtualClock.UtcNow,
                    LogLevel.Warning,
                    persona.DisplayName,
                    persona.Id,
                    "Discovery",
                    $"Failed to register visibility on relay '{persona.HomeRelay.DisplayName}': {ex.Message}"));
                return false;
            }
        }

        _stateBus.Publish(new SimulatorStateSignal(
            persona.Id,
            SignalCategory.DiscoveryStateChanged,
            Details: isDiscoverable ? "Discoverable" : "Hidden"));

        return true;
    }

    public async Task<BlindedLocator> PublishBlindedLocatorAsync(
        ISimulatedPersonaHost persona,
        ISimulatedRelayHost relay,
        byte[] sharedSecret,
        string? routeDescriptor = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(persona);
        ArgumentNullException.ThrowIfNull(relay);
        ArgumentNullException.ThrowIfNull(sharedSecret);

        var epoch = new DiscoveryEpoch(_virtualClock.CurrentDiscoveryEpoch);
        var locator = BlindedLocator.Derive(persona.PublicIdentityId, sharedSecret, epoch);
        var descriptor = string.IsNullOrWhiteSpace(routeDescriptor)
            ? $"https://127.0.0.1:{persona.BoundPort}"
            : routeDescriptor;

        using var scope = persona.Services.CreateScope();
        var channelFactory = scope.ServiceProvider.GetRequiredService<IPeerGrpcChannelFactory>();
        var relayUri = new Uri($"https://127.0.0.1:{relay.BoundPort}");
        var channel = channelFactory.GetOrCreateChannel(relayUri);
        var client = new PrivateRendezvousRelayService.PrivateRendezvousRelayServiceClient(channel);

        await client.PublishRendezvousAsync(new BlindedRendezvousPublishProto
        {
            BlindedLocator = ByteString.CopyFrom(locator.Span),
            RouteDescriptor = descriptor
        }, cancellationToken: ct).ConfigureAwait(false);

        _logSink.Log(new SimulatorLogEntry(
            _virtualClock.UtcNow,
            LogLevel.Information,
            persona.DisplayName,
            persona.Id,
            "Rendezvous",
            $"Published blinded locator (Epoch: {epoch.Value}, Locator: {Convert.ToHexString(locator.Span)[..8]}...) to relay '{relay.DisplayName}'."));

        _stateBus.Publish(new SimulatorStateSignal(
            persona.Id,
            SignalCategory.DiscoveryStateChanged,
            Details: "BlindedLocatorPublished"));

        return locator;
    }

    public async Task<IReadOnlyList<BlindedRendezvousMatchProto>> ResolveBlindedLocatorAsync(
        ISimulatedPersonaHost persona,
        ISimulatedRelayHost relay,
        PublicIdentityId targetPeerId,
        byte[] sharedSecret,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(persona);
        ArgumentNullException.ThrowIfNull(relay);
        ArgumentNullException.ThrowIfNull(sharedSecret);

        var currentEpoch = new DiscoveryEpoch(_virtualClock.CurrentDiscoveryEpoch);
        var window = BlindedLocator.DeriveWindow(targetPeerId, sharedSecret, currentEpoch);

        using var scope = persona.Services.CreateScope();
        var channelFactory = scope.ServiceProvider.GetRequiredService<IPeerGrpcChannelFactory>();
        var relayUri = new Uri($"https://127.0.0.1:{relay.BoundPort}");
        var channel = channelFactory.GetOrCreateChannel(relayUri);
        var client = new PrivateRendezvousRelayService.PrivateRendezvousRelayServiceClient(channel);

        var query = new BlindedRendezvousQueryProto();
        foreach (var loc in window)
        {
            query.BlindedLocators.Add(ByteString.CopyFrom(loc.Span));
        }

        var response = await client.QueryRendezvousAsync(query, cancellationToken: ct).ConfigureAwait(false);
        var matches = response.Matches.ToList();

        _resolvedLocators[persona.PublicIdentityId] = matches;

        _logSink.Log(new SimulatorLogEntry(
            _virtualClock.UtcNow,
            LogLevel.Information,
            persona.DisplayName,
            persona.Id,
            "Rendezvous",
            $"Resolved {matches.Count} match(es) for peer '{targetPeerId}' on relay '{relay.DisplayName}' (Epoch: {currentEpoch.Value})."));

        _stateBus.Publish(new SimulatorStateSignal(
            persona.Id,
            SignalCategory.DiscoveryStateChanged,
            Details: "BlindedLocatorResolved"));

        return matches;
    }

    public async Task<IReadOnlyList<PeerDiscoveryDescriptorProto>> QueryNearbyPeersAsync(
        ISimulatedPersonaHost persona,
        ISimulatedRelayHost relay,
        int limit = 10,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(persona);
        ArgumentNullException.ThrowIfNull(relay);

        using var scope = persona.Services.CreateScope();
        var channelFactory = scope.ServiceProvider.GetRequiredService<IPeerGrpcChannelFactory>();
        var relayUri = new Uri($"https://127.0.0.1:{relay.BoundPort}");
        var channel = channelFactory.GetOrCreateChannel(relayUri);
        var client = new NearbyDiscoveryService.NearbyDiscoveryServiceClient(channel);

        var response = await client.QueryNearbyAsync(new NearbyDiscoveryQueryProto
        {
            RequesterId = ByteString.CopyFrom(persona.PublicIdentityId.ToByteArray()),
            RequestedLimit = limit
        }, cancellationToken: ct).ConfigureAwait(false);

        // Filter out ourselves
        var results = response.Descriptors
            .Where(d => !d.PeerId.Span.SequenceEqual(persona.PublicIdentityId.ToByteArray()))
            .ToList();

        _lastDiscoveredPeers[persona.PublicIdentityId] = results;

        _logSink.Log(new SimulatorLogEntry(
            _virtualClock.UtcNow,
            LogLevel.Information,
            persona.DisplayName,
            persona.Id,
            "Discovery",
            $"Query nearby on relay '{relay.DisplayName}' returned {results.Count} peer candidate(s)."));

        _stateBus.Publish(new SimulatorStateSignal(
            persona.Id,
            SignalCategory.DiscoveryStateChanged,
            Details: $"NearbyDiscovered:{results.Count}"));

        return results;
    }

    public async Task<bool> ConnectDiscoveredPeerAsync(
        ISimulatedPersonaHost initiator,
        PublicIdentityId candidatePeerId,
        string? relayEndpoint = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(initiator);

        var targetHost = _worldState.FindPersonaHostByIdentity(candidatePeerId);
        if (targetHost != null)
        {
            if (!string.IsNullOrWhiteSpace(relayEndpoint))
            {
                var relayHost = _worldState.GetAllRelayHosts()
                    .FirstOrDefault(r => $"https://127.0.0.1:{r.BoundPort}" == relayEndpoint || r.BoundPort.ToString() == relayEndpoint);
                if (relayHost != null)
                {
                    return await _relayCoordinator.InitiateRelayedHandshakeAsync(initiator.Id, targetHost.Id, relayHost.Id, ct).ConfigureAwait(false);
                }
            }

            return await _handshakeOrchestrator.InitiateDirectHandshakeAsync(initiator.Id, targetHost.Id, ct).ConfigureAwait(false);
        }

        _logSink.Log(new SimulatorLogEntry(
            _virtualClock.UtcNow,
            LogLevel.Warning,
            initiator.DisplayName,
            initiator.Id,
            "Discovery",
            $"Discovered peer '{candidatePeerId}' target host could not be located for handshake."));

        return false;
    }

    public IReadOnlyList<BlindedRendezvousMatchProto> GetResolvedMatches(PublicIdentityId observerId)
    {
        return _resolvedLocators.TryGetValue(observerId, out var list) ? list : Array.Empty<BlindedRendezvousMatchProto>();
    }

    public IReadOnlyList<PeerDiscoveryDescriptorProto> GetLastDiscoveredPeers(PublicIdentityId observerId)
    {
        return _lastDiscoveredPeers.TryGetValue(observerId, out var list) ? list : Array.Empty<PeerDiscoveryDescriptorProto>();
    }
}
