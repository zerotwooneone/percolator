using System;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Percolator.Application2.Ports;
using Percolator.Application2.Profiles;
using Percolator.Domain.Channels.Model;
using Percolator.Domain.Channels.Ports;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Common;
using Percolator.Domain.Identities.Model;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Simulator.Features.Logging;
using Percolator.Simulator.Features.Shell;
using Percolator.Simulator.Features.Topology;

namespace Percolator.Simulator.Features.DirectChat;

internal static class ChannelIdUtility
{
    public static ChannelId DeterministicFor(PublicIdentityId a, PublicIdentityId b)
    {
        var (first, second) = a.Value.CompareTo(b.Value) < 0 ? (a, b) : (b, a);
        Span<byte> combined = stackalloc byte[32];
        first.TryWriteBytes(combined[..16]);
        second.TryWriteBytes(combined[16..]);
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(combined, hash);
        return ChannelId.FromBytes(hash[..16]);
    }
}

internal sealed class DirectHandshakeOrchestrator
{
    private readonly ISimulatedWorldStateService _worldState;
    private readonly ISimulatorStateBus _stateBus;
    private readonly IStatusToastService _toastService;
    private readonly ISimulatorLogSink _logSink;

    public DirectHandshakeOrchestrator(
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
                var targetBundle = target.CreatePreKeyBundle();

                using var scope = initiator.Services.CreateScope();
                var handshakeService = scope.ServiceProvider.GetRequiredService<IHandshakeService>();

                var result = await handshakeService.InitiateHandshakeAsync(
                    initiator.PublicIdentityId,
                    DeviceId.Primary,
                    targetBundle,
                    initialAppId: null,
                    initialPayload: null,
                    ct: ct);

                if (!result.IsSuccess)
                {
                    _toastService.ShowToast("Handshake Failed", result.Error.Description, ToastSeverity.Error);
                    return false;
                }

                // Ensure channel exists on initiator side
                var channelId = ChannelIdUtility.DeterministicFor(initiator.PublicIdentityId, target.PublicIdentityId);
                var channelRepo = scope.ServiceProvider.GetRequiredService<IChannelRepository>();
                var timeProvider = scope.ServiceProvider.GetRequiredService<IDateTimeProvider>();
                var existingChannel = await channelRepo.GetDirectByIdAsync(channelId, ct);
                if (existingChannel == null)
                {
                    var channel = DirectChannel.Create(channelId, initiator.PublicIdentityId, target.PublicIdentityId, timeProvider).Value!;
                    await channelRepo.SaveDirectAsync(channel, ct);
                }

                // Immediately flush outbox to deliver the handshake packet via loopback gRPC TLS
                var flushResult = await initiator.FlushOutboxAsync(ct);
                if (!flushResult.IsSuccess)
                {
                    _toastService.ShowToast("Handshake Delivery Failed", flushResult.Error.Description, ToastSeverity.Error);
                    return false;
                }

                _logSink.Log(new SimulatorLogEntry(
                    DateTimeOffset.UtcNow,
                    LogLevel.Information,
                    initiator.DisplayName,
                    initiator.Id,
                    "Handshake",
                    $"Direct handshake invitation sent to '{target.DisplayName}'."));

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
                using var scope = target.Services.CreateScope();
                var contactCoord = scope.ServiceProvider.GetRequiredService<IContactRequestCoordinator>();
                var handshakeService = scope.ServiceProvider.GetRequiredService<IHandshakeService>();
                var timeProvider = scope.ServiceProvider.GetRequiredService<IDateTimeProvider>();
                var channelRepo = scope.ServiceProvider.GetRequiredService<IChannelRepository>();

                var approveResult = await contactCoord.ApproveRequestAsync(target.PublicIdentityId, senderId, PeerTrustLevel.Tofu, ct);
                if (!approveResult.IsSuccess)
                {
                    _toastService.ShowToast("Accept Failed", approveResult.Error.Description, ToastSeverity.Error);
                    return false;
                }

                var completeResult = await handshakeService.CompletePendingHandshakeAsync(target.PublicIdentityId, senderId, ct);
                if (!completeResult.IsSuccess)
                {
                    _toastService.ShowToast("Accept Failed", completeResult.Error.Description, ToastSeverity.Error);
                    return false;
                }

                // Ensure channel exists on target
                var channelId = ChannelIdUtility.DeterministicFor(target.PublicIdentityId, senderId);
                var directChannel = await channelRepo.GetDirectByIdAsync(channelId, ct);
                if (directChannel == null)
                {
                    var channel = DirectChannel.Create(channelId, target.PublicIdentityId, senderId, timeProvider).Value!;
                    await channelRepo.SaveDirectAsync(channel, ct);
                }

                // Flush outbox in case any reply packets were queued
                await target.FlushOutboxAsync(ct);

                var senderHost = _worldState.FindPersonaHostByIdentity(senderId);
                if (senderHost != null)
                {
                    // Ensure sender also has the channel created
                    using var senderScope = senderHost.Services.CreateScope();
                    var senderChannelRepo = senderScope.ServiceProvider.GetRequiredService<IChannelRepository>();
                    var senderTimeProvider = senderScope.ServiceProvider.GetRequiredService<IDateTimeProvider>();
                    var sc = await senderChannelRepo.GetDirectByIdAsync(channelId, ct);
                    if (sc == null)
                    {
                        var channel = DirectChannel.Create(channelId, senderId, target.PublicIdentityId, senderTimeProvider).Value!;
                        await senderChannelRepo.SaveDirectAsync(channel, ct);
                    }
                    await senderHost.FlushOutboxAsync(ct);

                    _stateBus.Publish(new SimulatorStateSignal(
                        senderHost.Id,
                        SignalCategory.RatchetSessionChanged,
                        Details: target.PublicIdentityId.ToString()));
                }

                _logSink.Log(new SimulatorLogEntry(
                    DateTimeOffset.UtcNow,
                    LogLevel.Information,
                    target.DisplayName,
                    target.Id,
                    "Handshake",
                    $"Accepted handshake from {senderId}. Double Ratchet session established."));

                _toastService.ShowToast("Handshake Accepted", "✓ Direct secure session established.", ToastSeverity.Success);

                _stateBus.Publish(new SimulatorStateSignal(
                    target.Id,
                    SignalCategory.PendingHandshakeChanged,
                    Details: senderId.ToString()));

                _stateBus.Publish(new SimulatorStateSignal(
                    target.Id,
                    SignalCategory.RatchetSessionChanged,
                    Details: senderId.ToString()));

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
                var contactCoord = scope.ServiceProvider.GetRequiredService<IContactRequestCoordinator>();
                var pendingRepo = scope.ServiceProvider.GetRequiredService<IPendingHandshakeRepository>();

                await contactCoord.RejectRequestAsync(target.PublicIdentityId, senderId, block, ct);
                await pendingRepo.DeletePendingHandshakeAsync(target.PublicIdentityId, senderId, ct);

                _logSink.Log(new SimulatorLogEntry(
                    DateTimeOffset.UtcNow,
                    LogLevel.Information,
                    target.DisplayName,
                    target.Id,
                    "Handshake",
                    $"Rejected handshake from {senderId} (Blocked: {block})."));

                _toastService.ShowToast(
                    block ? "Handshake Blocked" : "Handshake Rejected",
                    $"Handshake from {senderId} has been {(block ? "blocked" : "rejected")}.",
                    ToastSeverity.Warning);

                _stateBus.Publish(new SimulatorStateSignal(
                    target.Id,
                    SignalCategory.PendingHandshakeChanged,
                    Details: senderId.ToString()));

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
