using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Percolator.Application.Delivery.Ports;
using Percolator.Application.Ports;
using Percolator.Domain.Common;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.Ports;
using Percolator.Infrastructure.Protos;
using Percolator.Infrastructure.Transport;
using Percolator.PluginSdk;
using Percolator.PluginSdk.Profiles;
using Percolator.Simulator.Features.Logging;
using Percolator.Simulator.Features.Shell;
using Percolator.Simulator.Features.Topology;

namespace Percolator.Simulator.Features.RelayedChat;

internal sealed class RelayProvisioningCoordinator
{
    private readonly ISimulatedWorldStateService _worldState;
    private readonly ISimulatorStateBus _stateBus;
    private readonly IStatusToastService _toastService;
    private readonly ISimulatorLogSink _logSink;

    public RelayProvisioningCoordinator(
        ISimulatedWorldStateService worldState,
        ISimulatorStateBus stateBus,
        IStatusToastService toastService,
        ISimulatorLogSink logSink)
    {
        _worldState = worldState ?? throw new ArgumentNullException(nameof(worldState));
        _stateBus = stateBus ?? throw new ArgumentNullException(nameof(stateBus));
        _toastService = toastService ?? throw new ArgumentNullException(nameof(toastService));
        _logSink = logSink ?? throw new ArgumentNullException(nameof(logSink));
    }

    public async Task<bool> RegisterMailboxAsync(
        ISimulatedPersonaHost persona,
        ISimulatedRelayHost relay,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(persona);
        ArgumentNullException.ThrowIfNull(relay);

        try
        {
            using (var scope = persona.Services.CreateScope())
            {
                var relayDirectory = scope.ServiceProvider.GetRequiredService<IRelayDirectory>();
                var provisioner = scope.ServiceProvider.GetRequiredService<IRelayMailboxProvisioner>();
                var relayEndpoint = RelayEndpoint.FromHostAndPort("127.0.0.1", relay.BoundPort);

                var registerResult = await relayDirectory.RegisterRemoteRelayAsync(
                    relay.RelayIdentityId, relayEndpoint, ct: ct);
                if (registerResult.IsFailure)
                {
                    _toastService.ShowToast("Registration Failed", registerResult.Error.Description, ToastSeverity.Error);
                    return false;
                }

                var linkResult = await relayDirectory.LinkIdentityAsync(
                    relay.RelayIdentityId,
                    persona.PublicIdentityId,
                    persona.MailboxRoutingToken,
                    persona.MailboxDrainToken,
                    ct);
                if (linkResult.IsFailure)
                {
                    _toastService.ShowToast("Registration Failed", linkResult.Error.Description, ToastSeverity.Error);
                    return false;
                }

                var tokensResult = await relayDirectory.GetMailboxTokensAsync(
                    persona.PublicIdentityId, relay.RelayIdentityId, ct);
                if (tokensResult.IsFailure)
                {
                    _toastService.ShowToast("Registration Failed", tokensResult.Error.Description, ToastSeverity.Error);
                    return false;
                }
                var tokens = tokensResult.Value!;

                var provisionResult = await provisioner.ProvisionMailboxAsync(
                    persona.PublicIdentityId, relay.RelayIdentityId, relayEndpoint, tokens, ct);
                if (provisionResult.IsFailure)
                {
                    _toastService.ShowToast("Registration Failed", "Relay rejected mailbox registration.", ToastSeverity.Error);
                    return false;
                }

                // Publish the persona's signed pre-key bundle over gRPC with the mailbox
                // tokens so (a) owner association happens on the wire rather than through
                // an in-memory store call, and (b) the peer directory's pre-key admission
                // gate is satisfied before visibility registration.
                if (!await relay.QueryService.HasActivePreKeyAsync(persona.MailboxRoutingToken, ct))
                {
                    var publishResult = await provisioner.PublishPreKeyBundleAsync(
                        persona.PublicIdentityId, DeviceId.Primary, relayEndpoint, tokens, 0, ct);
                    if (publishResult.IsFailure)
                    {
                        _toastService.ShowToast("Publish Error", "Relay rejected initial PreKey bundle publication.", ToastSeverity.Error);
                        return false;
                    }
                }
            }

            persona.HomeRelay = relay;
            await persona.PublishProfileAsync(ct);

            using (var scope = persona.Services.CreateScope())
            {
                var channelFactory = scope.ServiceProvider.GetRequiredService<IPeerGrpcChannelFactory>();
                var cryptoEngine = scope.ServiceProvider.GetRequiredService<ICryptoEngine>();
                var timeProvider = scope.ServiceProvider.GetRequiredService<IDateTimeProvider>();
                var relayUri = new Uri($"https://127.0.0.1:{relay.BoundPort}");
                var channel = channelFactory.GetOrCreateChannel(relayUri);
                var discoveryClient = new NearbyDiscoveryService.NearbyDiscoveryServiceClient(channel);

                int visibility = persona.AllowPeerDiscovery ? 1 : 0;
                long timestampUnixMs = timeProvider.UtcNow.ToUnixTimeMilliseconds();
                // Advertise the mailbox capability tokens only when discoverable; the
                // signature binds them so the relay cannot substitute its own mailbox.
                byte[]? routingToken = persona.AllowPeerDiscovery ? persona.MailboxRoutingToken.ToByteArray() : null;
                byte[]? deliveryToken = persona.AllowPeerDiscovery ? persona.MailboxDeliveryToken.Memory.ToArray() : null;
                var payload = DiscoveryWireFraming.BuildVisibilitySignaturePayload(
                    persona.PublicIdentityId, visibility, timestampUnixMs, routingToken, deliveryToken);
                var signature = cryptoEngine.SignEd25519(persona.IdentityPrivateKey.Span, payload);

                await discoveryClient.RegisterVisibilityAsync(new DiscoveryRegistrationProto
                {
                    PeerId = ByteString.CopyFrom(persona.PublicIdentityId.ToByteArray()),
                    Visibility = visibility,
                    TimestampUnixMs = timestampUnixMs,
                    Signature = ByteString.CopyFrom(signature),
                    IdentityKey = ByteString.CopyFrom(persona.IdentityPublicKey.Span),
                    RoutingToken = routingToken != null ? ByteString.CopyFrom(routingToken) : ByteString.Empty,
                    DeliveryToken = deliveryToken != null ? ByteString.CopyFrom(deliveryToken) : ByteString.Empty
                }, cancellationToken: ct);
            }

            _logSink.Log(new SimulatorLogEntry(
                DateTimeOffset.UtcNow,
                LogLevel.Information,
                persona.DisplayName,
                persona.Id,
                "RelayProvisioning",
                $"Mailbox registered on relay '{relay.DisplayName}' (Port: {relay.BoundPort})."));

            return true;
        }
        catch (Exception ex)
        {
            _logSink.Log(new SimulatorLogEntry(
                DateTimeOffset.UtcNow,
                LogLevel.Error,
                persona.DisplayName,
                persona.Id,
                "RelayProvisioning",
                $"Failed to register mailbox: {ex.Message}",
                ex.ToString()));
            return false;
        }
    }

    public async Task<bool> PublishPreKeysAsync(
        ISimulatedPersonaHost persona,
        ISimulatedRelayHost relay,
        int count = 25,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(persona);
        ArgumentNullException.ThrowIfNull(relay);

        try
        {
            // 1. Ensure mailbox is registered and the persona is linked to the relay
            if (!await RegisterMailboxAsync(persona, relay, ct))
            {
                return false;
            }

            // 2. Delegate bundle publication (OPK generation, persistence and signing)
            // to the production IRelayMailboxProvisioner.
            using (var scope = persona.Services.CreateScope())
            {
                var relayDirectory = scope.ServiceProvider.GetRequiredService<IRelayDirectory>();
                var provisioner = scope.ServiceProvider.GetRequiredService<IRelayMailboxProvisioner>();
                var relayEndpoint = RelayEndpoint.FromHostAndPort("127.0.0.1", relay.BoundPort);

                var tokensResult = await relayDirectory.GetMailboxTokensAsync(
                    persona.PublicIdentityId, relay.RelayIdentityId, ct);
                if (tokensResult.IsFailure)
                {
                    _logSink.Log(new SimulatorLogEntry(
                        DateTimeOffset.UtcNow,
                        LogLevel.Error,
                        persona.DisplayName,
                        persona.Id,
                        "RelayProvisioning",
                        $"Failed to resolve mailbox tokens: {tokensResult.Error.Code} - {tokensResult.Error.Description}"));
                    _toastService.ShowToast("Publish Error", tokensResult.Error.Description, ToastSeverity.Error);
                    return false;
                }

                var publishResult = await provisioner.PublishPreKeyBundleAsync(
                    persona.PublicIdentityId, DeviceId.Primary, relayEndpoint, tokensResult.Value!, count, ct);
                if (publishResult.IsFailure)
                {
                    _logSink.Log(new SimulatorLogEntry(
                        DateTimeOffset.UtcNow,
                        LogLevel.Error,
                        persona.DisplayName,
                        persona.Id,
                        "RelayProvisioning",
                        $"PreKey bundle publication failed: {publishResult.Error.Code} - {publishResult.Error.Description}"));
                    _toastService.ShowToast("Publish Error", "Relay rejected PreKey bundle publication.", ToastSeverity.Error);
                    return false;
                }
            }

            _logSink.Log(new SimulatorLogEntry(
                DateTimeOffset.UtcNow,
                LogLevel.Information,
                persona.DisplayName,
                persona.Id,
                "RelayProvisioning",
                $"Published {count} OPKs to relay '{relay.DisplayName}'."));

            return true;
        }
        catch (Exception ex)
        {
            _logSink.Log(new SimulatorLogEntry(
                DateTimeOffset.UtcNow,
                LogLevel.Error,
                persona.DisplayName,
                persona.Id,
                "RelayProvisioning",
                $"Failed to publish prekeys: {ex.Message}",
                ex.ToString()));
            return false;
        }
    }

    public async Task<int> BulkRegisterMailboxesAsync(
        IEnumerable<ISimulatedPersonaHost> personas,
        ISimulatedRelayHost relay,
        CancellationToken ct = default)
    {
        int count = 0;
        foreach (var p in personas)
        {
            if (await RegisterMailboxAsync(p, relay, ct))
            {
                count++;
            }
        }
        return count;
    }

    public async Task<int> BulkPublishPreKeysAsync(
        IEnumerable<ISimulatedPersonaHost> personas,
        ISimulatedRelayHost relay,
        int opkCount = 25,
        CancellationToken ct = default)
    {
        int count = 0;
        foreach (var p in personas)
        {
            if (await PublishPreKeysAsync(p, relay, opkCount, ct))
            {
                count++;
            }
        }
        return count;
    }

    public async Task<bool> InitiateRelayedHandshakeAsync(
        SimulatedNodeId initiatorNodeId,
        SimulatedNodeId targetNodeId,
        SimulatedNodeId? relayNodeId = null,
        CancellationToken ct = default)
    {
        var initiator = _worldState.GetPersonaHost(initiatorNodeId);
        var target = _worldState.GetPersonaHost(targetNodeId);

        if (initiator == null || target == null)
        {
            _toastService.ShowToast("Handshake Failed", "Both initiator and target must be active.", ToastSeverity.Error);
            return false;
        }

        ISimulatedRelayHost? relay = null;
        if (relayNodeId.HasValue)
        {
            relay = _worldState.GetRelayHost(relayNodeId.Value);
        }
        else if (target.HomeRelay != null)
        {
            relay = target.HomeRelay;
        }
        else
        {
            relay = _worldState.GetAllRelayHosts().FirstOrDefault();
        }

        if (relay == null)
        {
            _toastService.ShowToast("Relay Required", "No active relay found. Add a Relay first.", ToastSeverity.Warning);
            return false;
        }

        try
        {
            // 1. Ensure target has mailbox registered and prekeys published on this relay
            if (target.HomeRelay == null || !await relay.QueryService.HasActivePreKeyAsync(target.MailboxRoutingToken, ct))
            {
                await PublishPreKeysAsync(target, relay, 25, ct);
            }

            // 2. Ensure initiator also has mailbox registered on this relay
            if (initiator.HomeRelay == null)
            {
                await RegisterMailboxAsync(initiator, relay, ct);
            }

            // 3. Save the initiator's routing capability for the target FIRST so the
            // pre-key provider can resolve the delivery capability from storage.
            var relayEndpoint = RelayEndpoint.FromHostAndPort("127.0.0.1", relay.BoundPort);

            using (var scope = initiator.Services.CreateScope())
            {
                var routingRepo = scope.ServiceProvider.GetRequiredService<IPeerMailboxRoutingRepository>();
                var cap = new PeerMailboxRoutingCapability(
                    target.PublicIdentityId,
                    target.MailboxRoutingToken,
                    target.MailboxDeliveryToken,
                    relayEndpoint,
                    LastObservedAtUtc: DateTimeOffset.UtcNow);
                await routingRepo.SaveAsync(cap, ct);
            }

            // 4. Fetch consumable PreKeyBundle via IPreKeyBundleProvider (consumes 1 OPK)
            PreKeyBundle remoteBundle;
            using (var scope = initiator.Services.CreateScope())
            {
                var bundleProvider = scope.ServiceProvider.GetRequiredService<IPreKeyBundleProvider>();
                var bundleResult = await bundleProvider.FetchConsumableBundleAsync(
                    target.PublicIdentityId, DeviceId.Primary, relayEndpoint, ct);
                if (bundleResult.IsFailure)
                {
                    if (bundleResult.Error.Description?.Contains("Unable to consume") == true)
                    {
                        _toastService.ShowToast("OPK Depleted", "No available one-time prekeys left on relay.", ToastSeverity.Warning);
                    }
                    else
                    {
                        _toastService.ShowToast("PreKey Missing", $"Failed to fetch prekey from relay: {bundleResult.Error.Description}", ToastSeverity.Error);
                    }
                    return false;
                }
                remoteBundle = bundleResult.Value!;
            }

            // 5. Save the target's routing capability for the initiator
            using (var scope = target.Services.CreateScope())
            {
                var routingRepo = scope.ServiceProvider.GetRequiredService<IPeerMailboxRoutingRepository>();
                var cap = new PeerMailboxRoutingCapability(
                    initiator.PublicIdentityId,
                    initiator.MailboxRoutingToken,
                    initiator.MailboxDeliveryToken,
                    relayEndpoint,
                    LastObservedAtUtc: DateTimeOffset.UtcNow);
                await routingRepo.SaveAsync(cap, ct);
            }

            // 5. Initiator initiates X3DH handshake
            using (var scope = initiator.Services.CreateScope())
            {
                var handshakeService = scope.ServiceProvider.GetRequiredService<IHandshakeService>();
                var hsResult = await handshakeService.InitiateHandshakeAsync(
                    initiator.PublicIdentityId,
                    DeviceId.Primary,
                    target.PublicIdentityId,
                    remoteBundle,
                    ct: ct);

                if (!hsResult.IsSuccess)
                {
                    _toastService.ShowToast("Handshake Failed", hsResult.Error.Description, ToastSeverity.Error);
                    return false;
                }
            }

            // 6. Flush initiator outbox -> dispatches envelope to Relay's UnauthenticatedDeliveryEndpoint.EnqueueMailbox
            var flushResult = await initiator.FlushOutboxAsync(ct);
            if (!flushResult.IsSuccess)
            {
                _toastService.ShowToast("Outbox Failed", flushResult.Error.Description, ToastSeverity.Warning);
                return false;
            }

            _logSink.Log(new SimulatorLogEntry(
                DateTimeOffset.UtcNow,
                LogLevel.Information,
                initiator.DisplayName,
                initiator.Id,
                "RelayedHandshake",
                $"Relayed handshake initiated: '{initiator.DisplayName}' ➔ '{target.DisplayName}' via relay '{relay.DisplayName}'. Envelope queued."));

            return true;
        }
        catch (Exception ex)
        {
            _logSink.Log(new SimulatorLogEntry(
                DateTimeOffset.UtcNow,
                LogLevel.Error,
                initiator.DisplayName,
                initiator.Id,
                "RelayedHandshake",
                $"Relayed handshake exception: {ex.Message}",
                ex.ToString()));

            _toastService.ShowToast("Handshake Exception", ex.Message, ToastSeverity.Error);
            return false;
        }
    }
}
