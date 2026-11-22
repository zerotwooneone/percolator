using System;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Percolator.Application.Connections;
using Percolator.Application.Invitations;
using Percolator.Application.Ports;
using Percolator.Application.Profiles;
using Percolator.Domain.Channels.Model;
using Percolator.Domain.Channels.Ports;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Common;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities.Model;
using Percolator.Domain.Identities.Ports;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.ValueObjects;
using Percolator.Infrastructure.Persistence;
using Percolator.Infrastructure.Protos;
using Percolator.Infrastructure.Transport;
using Percolator.PluginSdk;
using Percolator.PluginSdk.Profiles;
using Percolator.Simulator.Features.Logging;
using Percolator.Simulator.Features.RichChatAndProfiles;
using Percolator.Simulator.Features.Scenarios;
using Percolator.Simulator.Features.Scenarios.Model;
using Percolator.Simulator.Features.Shell;
using Percolator.Simulator.Features.Topology;
using PublicIdentityId = Percolator.Domain.Identities.PublicIdentityId;

namespace Percolator.Simulator.Features.DirectChat;

internal sealed class DirectHandshakeOrchestrator
{
    private readonly ISimulatedWorldStateService _worldState;
    private readonly ISimulatorStateBus _stateBus;
    private readonly IStatusToastService _toastService;
    private readonly ISimulatorLogSink _logSink;
    private readonly ProfileSyncCoordinator _profileCoordinator;
    private readonly IFlightRecorderStateService _flightRecorder;

    public DirectHandshakeOrchestrator(
        ISimulatedWorldStateService worldState,
        ISimulatorStateBus stateBus,
        IStatusToastService toastService,
        ISimulatorLogSink logSink,
        ProfileSyncCoordinator profileCoordinator,
        IFlightRecorderStateService flightRecorder)
    {
        _worldState = worldState ?? throw new ArgumentNullException(nameof(worldState));
        _stateBus = stateBus ?? throw new ArgumentNullException(nameof(stateBus));
        _toastService = toastService ?? throw new ArgumentNullException(nameof(toastService));
        _logSink = logSink ?? throw new ArgumentNullException(nameof(logSink));
        _profileCoordinator = profileCoordinator ?? throw new ArgumentNullException(nameof(profileCoordinator));
        _flightRecorder = flightRecorder ?? throw new ArgumentNullException(nameof(flightRecorder));
    }

    public Task<bool> InitiateDirectHandshakeAsync(
        SimulatedNodeId initiatorNodeId,
        SimulatedNodeId targetNodeId,
        CancellationToken ct = default)
    {
        return Task.Run(async () =>
        {
            var initiator = _worldState.GetPersonaHost(initiatorNodeId);
            var target = _worldState.GetPersonaHost(targetNodeId);

            if (initiator == null || target == null)
            {
                _toastService.ShowToast("Handshake Failed", "Both initiator and target must be active personas.", ToastSeverity.Error);
                return false;
            }

            if (initiator.Id == target.Id)
            {
                _toastService.ShowToast("Invalid Handshake", "Cannot initiate handshake with self.", ToastSeverity.Warning);
                return false;
            }

            try
            {
                using var scope = initiator.Services.CreateScope();
                var workflow = scope.ServiceProvider.GetRequiredService<IOutboundConnectionWorkflow>();

                var payload = new PeerInvitationPayload(
                    target.PublicIdentityId,
                    target.MailboxRoutingToken,
                    target.MailboxDeliveryToken,
                    new[] { target.BoundEndpoint.Host },
                    Percolator.Domain.Identities.ValueObjects.PortRange.Single((ushort)target.BoundPort));

                var connectResult = await workflow.ConnectAsync(payload, TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
                if (!connectResult.IsSuccess)
                {
                    _toastService.ShowToast("Handshake Failed", connectResult.Error.Description, ToastSeverity.Error);
                    return false;
                }

                _logSink.Log(new SimulatorLogEntry(
                    DateTimeOffset.UtcNow,
                    LogLevel.Information,
                    initiator.DisplayName,
                    initiator.Id,
                    "Handshake",
                    $"Direct handshake invitation sent to '{target.DisplayName}'."));

                _flightRecorder.RecordStep(new InitiateDirectHandshakeStep(initiator.DisplayName, target.DisplayName));
                _toastService.ShowToast("Handshake Sent", $"⚡ Direct handshake sent to {target.DisplayName}.", ToastSeverity.Success);
                return true;
            }
            catch (Exception ex)
            {
                _logSink.Log(new SimulatorLogEntry(
                    DateTimeOffset.UtcNow,
                    LogLevel.Error,
                    initiator.DisplayName,
                    initiator.Id,
                    "Handshake",
                    $"Failed to initiate handshake: {ex.Message}",
                    ex.ToString()));

                _toastService.ShowToast("Handshake Error", ex.Message, ToastSeverity.Error);
                return false;
            }
        }, ct);
    }

    public Task<bool> AcceptHandshakeAsync(
        SimulatedNodeId targetNodeId,
        PublicIdentityId senderId,
        CancellationToken ct = default)
    {
        return Task.Run(async () =>
        {
            var target = _worldState.GetPersonaHost(targetNodeId);
            if (target == null) return false;

            try
            {
                var senderHost = _worldState.FindPersonaHostByIdentity(senderId);
                if (senderHost != null)
                {
                    var targetStreamRegistry = target.Services.GetRequiredService<IStreamRegistryManagement>();
                    targetStreamRegistry.RegisterDirectEndpoint(senderHost.PublicIdentityId, senderHost.BoundEndpoint);

                }

                using var scope = target.Services.CreateScope();
                var timeProvider = scope.ServiceProvider.GetRequiredService<IDateTimeProvider>();
                var routingRepo = scope.ServiceProvider.GetRequiredService<IPeerMailboxRoutingRepository>();

                var existingRoute = await routingRepo.GetByPeerIdAsync(senderId, ct).ConfigureAwait(false);
                if (existingRoute == null)
                {
                    if (senderHost != null && senderHost.HomeRelay != null)
                    {
                        var relayEndpoint = RelayEndpoint.FromHostAndPort(senderHost.HomeRelay.BoundEndpoint.Host, senderHost.HomeRelay.BoundPort);
                        var cap = new PeerMailboxRoutingCapability(
                            senderHost.PublicIdentityId,
                            senderHost.MailboxRoutingToken,
                            senderHost.MailboxDeliveryToken,
                            relayEndpoint,
                            timeProvider.UtcNow);
                        await routingRepo.SaveAsync(cap, ct).ConfigureAwait(false);
                    }
                    else if (target.HomeRelay != null && target.HomeRelay.PeerDirectory != null)
                    {
                        var reg = await target.HomeRelay.PeerDirectory.GetPeerAsync(senderId, ct).ConfigureAwait(false);
                        if (reg != null && reg.RoutingToken.HasValue && reg.DeliveryToken != null)
                        {
                            var relayEndpoint = RelayEndpoint.FromHostAndPort(target.HomeRelay.BoundEndpoint.Host, target.HomeRelay.BoundPort);
                            var cap = new PeerMailboxRoutingCapability(
                                senderId,
                                reg.RoutingToken.Value,
                                reg.DeliveryToken,
                                relayEndpoint,
                                timeProvider.UtcNow);
                            await routingRepo.SaveAsync(cap, ct).ConfigureAwait(false);
                        }
                    }
                }

                var inboundWorkflow = scope.ServiceProvider.GetRequiredService<IInboundConnectionWorkflow>();
                var acceptResult = await inboundWorkflow.AcceptRequestAsync(target.PublicIdentityId, senderId, ct);
                if (!acceptResult.IsSuccess)
                {
                    _toastService.ShowToast("Accept Failed", acceptResult.Error.Description, ToastSeverity.Error);
                    return false;
                }

                if (senderHost != null)
                {
                    if (senderHost.HomeRelay != null)
                    {
                        await senderHost.DrainMailboxAsync(ct);
                    }

                    _stateBus.Publish(new SimulatorStateSignal(
                        senderHost.Id,
                        SignalCategory.RatchetSessionChanged,
                        Details: target.PublicIdentityId.ToString(),
                        Payload: new HandshakeSignalPayload(target.PublicIdentityId, "Accept")));

                    // Auto-Share Profile on Connect
                    await _profileCoordinator.AutoShareProfileOnConnectAsync(target, senderHost, ct);
                }

                _logSink.Log(new SimulatorLogEntry(
                    DateTimeOffset.UtcNow,
                    LogLevel.Information,
                    target.DisplayName,
                    target.Id,
                    "Handshake",
                    $"Accepted handshake from {senderId}. Double Ratchet session established."));

                _flightRecorder.RecordStep(new ApprovePendingHandshakeStep(target.DisplayName, senderHost?.DisplayName ?? senderId.ToString()));
                _toastService.ShowToast("Handshake Accepted", "✓ Direct secure session established.", ToastSeverity.Success);

                _stateBus.Publish(new SimulatorStateSignal(
                    target.Id,
                    SignalCategory.RatchetSessionChanged,
                    Details: senderId.ToString(),
                    Payload: new HandshakeSignalPayload(senderId, "Accept")));

                return true;
            }
            catch (Exception ex)
            {
                _logSink.Log(new SimulatorLogEntry(
                    DateTimeOffset.UtcNow,
                    LogLevel.Error,
                    target.DisplayName,
                    target.Id,
                    "Handshake",
                    $"Error accepting handshake: {ex.Message}",
                    ex.ToString()));

                _toastService.ShowToast("Accept Error", ex.Message, ToastSeverity.Error);
                return false;
            }
        }, ct);
    }

    public Task<bool> RejectHandshakeAsync(
        SimulatedNodeId targetNodeId,
        PublicIdentityId senderId,
        bool block = false,
        CancellationToken ct = default)
    {
        return Task.Run(async () =>
        {
            var target = _worldState.GetPersonaHost(targetNodeId);
            if (target == null) return false;

            try
            {
                using var scope = target.Services.CreateScope();
                var workflow = scope.ServiceProvider.GetRequiredService<IInboundConnectionWorkflow>();
                var result = block
                    ? await workflow.BlockRequestAsync(target.PublicIdentityId, senderId, ct)
                    : await workflow.RejectRequestAsync(target.PublicIdentityId, senderId, ct);

                if (!result.IsSuccess)
                {
                    _toastService.ShowToast("Reject Failed", result.Error.Description, ToastSeverity.Error);
                    return false;
                }

                var senderHost = _worldState.FindPersonaHostByIdentity(senderId);

                _logSink.Log(new SimulatorLogEntry(
                    DateTimeOffset.UtcNow,
                    LogLevel.Information,
                    target.DisplayName,
                    target.Id,
                    "Handshake",
                    $"Rejected handshake from {senderId} (Blocked: {block})."));

                _flightRecorder.RecordStep(new RejectPendingHandshakeStep(target.DisplayName, senderHost?.DisplayName ?? senderId.ToString(), block));

                _toastService.ShowToast(
                    block ? "Handshake Blocked" : "Handshake Rejected",
                    $"Handshake from {senderId} has been {(block ? "blocked" : "rejected")}.",
                    ToastSeverity.Warning);

                return true;
            }
            catch (Exception ex)
            {
                _toastService.ShowToast("Reject Error", ex.Message, ToastSeverity.Error);
                return false;
            }
        }, ct);
    }
}
