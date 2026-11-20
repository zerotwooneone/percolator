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
using Percolator.Domain.Channels.Model;
using Percolator.Domain.Channels.Ports;
using Percolator.Domain.Identities.Model;
using Percolator.Domain.Identities.Ports;
using Percolator.Infrastructure.Protos;
using Percolator.Infrastructure.Transport;
using Percolator.PluginSdk;
using Percolator.PluginSdk.Profiles;
using Percolator.Simulator.Features.DirectChat;
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
            // AssignHomeRelayAsync owns registration + linking (it skips re-registering
            // an already-known relay, preserving the binding's provisioned mark that
            // an unconditional RegisterRemoteRelayAsync would clobber).
            await persona.AssignHomeRelayAsync(relay, ct);

            // Provision-on-connect is the drain scheduler's boundary (DRAIN-07). The
            // coordinator still awaits the same ensure here: the prekey gate and
            // visibility registration below require the mailbox capability to exist
            // relay-side, and the scheduler's call is idempotent against the mark.
            using (var ensureScope = persona.Services.CreateScope())
            {
                var directory = ensureScope.ServiceProvider.GetRequiredService<IRelayDirectory>();
                var ensured = await directory.EnsureMailboxProvisionedAsync(
                    relay.RelayIdentityId, persona.PublicIdentityId, ct);
                if (ensured.IsFailure)
                {
                    _toastService.ShowToast("Registration Failed",
                        $"Relay rejected mailbox registration: {ensured.Error.Description}", ToastSeverity.Error);
                    return false;
                }
            }

            // The relay's peer-directory admission gate rejects visibility registration
            // for candidates with no active signed prekey — publish the initial bundle
            // (0 OPKs) when absent. This is the discovery gate, not mailbox provisioning;
            // OPK replenishment stays in the explicit PublishPreKeys step.
            if (!await relay.QueryService.HasActivePreKeyAsync(persona.MailboxRoutingToken, ct))
            {
                using var gateScope = persona.Services.CreateScope();
                var gateDirectory = gateScope.ServiceProvider.GetRequiredService<IRelayDirectory>();
                var provisioner = gateScope.ServiceProvider.GetRequiredService<IRelayMailboxProvisioner>();
                var relayEndpoint = RelayEndpoint.FromHostAndPort(relay.BoundEndpoint.Host, relay.BoundPort);

                var tokensResult = await gateDirectory.GetMailboxTokensAsync(
                    persona.PublicIdentityId, relay.RelayIdentityId, ct);
                if (tokensResult.IsFailure)
                {
                    _toastService.ShowToast("Registration Failed", tokensResult.Error.Description, ToastSeverity.Error);
                    return false;
                }

                var publishResult = await provisioner.PublishPreKeyBundleAsync(
                    persona.PublicIdentityId, DeviceId.Primary, relayEndpoint, tokensResult.Value!, 0u, ct);
                if (publishResult.IsFailure)
                {
                    _toastService.ShowToast("Publish Error", "Relay rejected initial PreKey bundle publication.", ToastSeverity.Error);
                    return false;
                }
            }

            await persona.PublishProfileAsync(ct);

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
        int count,
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
                var relayEndpoint = RelayEndpoint.FromHostAndPort(relay.BoundEndpoint.Host, relay.BoundPort);

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
                    persona.PublicIdentityId, DeviceId.Primary, relayEndpoint, tokensResult.Value!, (uint)count, ct);
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
        int opkCount,
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
            var relayEndpoint = RelayEndpoint.FromHostAndPort(relay.BoundEndpoint.Host, relay.BoundPort);

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

            // 4. Connect to peer via IPeerConnectionService (fetches consumable prekey, initiates X3DH, provisions direct channel and contact)
            using (var scope = initiator.Services.CreateScope())
            {
                var peerConnectionService = scope.ServiceProvider.GetRequiredService<IPeerConnectionService>();
                var connectResult = await peerConnectionService.ConnectToPeerAsync(
                    target.PublicIdentityId,
                    DeviceId.Primary,
                    relayEndpoint,
                    ct).ConfigureAwait(false);

                if (connectResult.IsFailure)
                {
                    if (connectResult.Error.Description?.Contains("Unable to consume") == true)
                    {
                        _toastService.ShowToast("OPK Depleted", "No available one-time prekeys left on relay.", ToastSeverity.Warning);
                    }
                    else
                    {
                        _toastService.ShowToast("Handshake Failed", connectResult.Error.Description ?? "Unknown error", ToastSeverity.Error);
                    }
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
