using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Percolator.Apps.Chat.Features.Messaging;
using Percolator.Apps.Chat.Features.Reactions;
using Percolator.Domain.Channels.Model;
using Percolator.Domain.Channels.Ports;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Common;
using Percolator.Domain.Delivery.Ports;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.PluginSdk;
using Percolator.PluginSdk.Profiles;
using Percolator.Simulator.Features.DirectChat;
using Percolator.Simulator.Features.Logging;
using Percolator.Simulator.Features.Shell;
using Percolator.Simulator.Features.Topology;

namespace Percolator.Simulator.Features.RichChatAndProfiles;

internal sealed class RichChatActionCoordinator
{
    private readonly ISimulatedWorldStateService _worldState;
    private readonly ISimulatorStateBus _stateBus;
    private readonly IStatusToastService _toastService;
    private readonly ISimulatorLogSink _logSink;

    public RichChatActionCoordinator(
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

    public async Task<bool> AddReactionAsync(
        SimulatedNodeId actorNodeId,
        SimulatedNodeId peerNodeId,
        Guid messageId,
        string emojiText,
        CancellationToken ct = default)
    {
        var actor = _worldState.GetPersonaHost(actorNodeId);
        var peer = _worldState.GetPersonaHost(peerNodeId);
        if (actor == null || peer == null) return false;

        var emojiResult = Emoji.Create(emojiText);
        if (!emojiResult.IsSuccess)
        {
            _toastService.ShowToast("Reaction Error", emojiResult.Error.Description, ToastSeverity.Warning);
            return false;
        }

        try
        {
            var channelId = ChannelIdUtility.DeterministicFor(actor.PublicIdentityId, peer.PublicIdentityId);
            using var scope = actor.Services.CreateScope();
            var chatService = scope.ServiceProvider.GetRequiredService<IChatService>();

            var route = DeliveryRoute.DirectP2P;
            var routingRepo = scope.ServiceProvider.GetRequiredService<IPeerMailboxRoutingRepository>();
            if (peer.HomeRelay != null)
            {
                var now = scope.ServiceProvider.GetRequiredService<IDateTimeProvider>().UtcNow;
                var bestRoute = await routingRepo.GetBestRouteAsync(peer.PublicIdentityId, now, ct);
                if (bestRoute != null)
                {
                    route = DeliveryRoute.RelayedOneToOne;
                }
            }

            var command = new AddReactionCommand(
                TargetPayloadId: PayloadId.FromGuid(messageId),
                ChannelId: channelId,
                AuthorId: actor.PublicIdentityId,
                RecipientIdentityId: peer.PublicIdentityId,
                RecipientDeviceId: DeviceId.Primary,
                Route: route,
                Emoji: emojiResult.Value);

            var result = await chatService.AddReactionAsync(command, ct);
            if (!result.IsSuccess)
            {
                _toastService.ShowToast("Reaction Error", result.Error.Description, ToastSeverity.Error);
                return false;
            }

            _stateBus.Publish(new SimulatorStateSignal(
                actor.Id,
                SignalCategory.ChatTranscriptChanged,
                Details: peer.PublicIdentityId.ToString(),
                Payload: new ChatTranscriptSignalPayload(peer.PublicIdentityId)));

            var flushResult = await actor.FlushOutboxAsync(ct);
            if (!flushResult.IsSuccess)
            {
                _toastService.ShowToast("Reaction Delivery Deferred", flushResult.Error.Description, ToastSeverity.Warning);
                return false;
            }

            if (route == DeliveryRoute.RelayedOneToOne && peer.HomeRelay != null)
            {
                await peer.DrainMailboxAsync(ct);
            }

            _logSink.Log(new SimulatorLogEntry(
                DateTimeOffset.UtcNow,
                LogLevel.Information,
                actor.DisplayName,
                actor.Id,
                "RichChat",
                $"Added reaction '{emojiText}' to message {messageId}."));

            return true;
        }
        catch (Exception ex)
        {
            _logSink.Log(new SimulatorLogEntry(
                DateTimeOffset.UtcNow,
                LogLevel.Error,
                actor.DisplayName,
                actor.Id,
                "RichChat",
                $"Failed to add reaction: {ex.Message}",
                ex.ToString()));

            _toastService.ShowToast("Reaction Exception", ex.Message, ToastSeverity.Error);
            return false;
        }
    }

    public async Task<bool> EditMessageAsync(
        SimulatedNodeId authorNodeId,
        SimulatedNodeId peerNodeId,
        Guid messageId,
        string newContent,
        CancellationToken ct = default)
    {
        var author = _worldState.GetPersonaHost(authorNodeId);
        var peer = _worldState.GetPersonaHost(peerNodeId);
        if (author == null || peer == null) return false;

        if (string.IsNullOrWhiteSpace(newContent)) return false;

        try
        {
            var channelId = ChannelIdUtility.DeterministicFor(author.PublicIdentityId, peer.PublicIdentityId);
            using var scope = author.Services.CreateScope();
            var chatService = scope.ServiceProvider.GetRequiredService<IChatService>();

            var route = DeliveryRoute.DirectP2P;
            var routingRepo = scope.ServiceProvider.GetRequiredService<IPeerMailboxRoutingRepository>();
            if (peer.HomeRelay != null)
            {
                var now = scope.ServiceProvider.GetRequiredService<IDateTimeProvider>().UtcNow;
                var bestRoute = await routingRepo.GetBestRouteAsync(peer.PublicIdentityId, now, ct);
                if (bestRoute != null)
                {
                    route = DeliveryRoute.RelayedOneToOne;
                }
            }

            var command = new EditMessageCommand(
                TargetPayloadId: PayloadId.FromGuid(messageId),
                ChannelId: channelId,
                AuthorId: author.PublicIdentityId,
                RecipientIdentityId: peer.PublicIdentityId,
                RecipientDeviceId: DeviceId.Primary,
                Route: route,
                NewContent: newContent);

            var result = await chatService.EditMessageAsync(command, ct);
            if (!result.IsSuccess)
            {
                _toastService.ShowToast("Edit Error", result.Error.Description, ToastSeverity.Error);
                return false;
            }

            _stateBus.Publish(new SimulatorStateSignal(
                author.Id,
                SignalCategory.ChatTranscriptChanged,
                Details: peer.PublicIdentityId.ToString(),
                Payload: new ChatTranscriptSignalPayload(peer.PublicIdentityId)));

            var flushResult = await author.FlushOutboxAsync(ct);
            if (!flushResult.IsSuccess)
            {
                _toastService.ShowToast("Edit Delivery Deferred", flushResult.Error.Description, ToastSeverity.Warning);
                return false;
            }

            if (route == DeliveryRoute.RelayedOneToOne && peer.HomeRelay != null)
            {
                await peer.DrainMailboxAsync(ct);
            }

            _logSink.Log(new SimulatorLogEntry(
                DateTimeOffset.UtcNow,
                LogLevel.Information,
                author.DisplayName,
                author.Id,
                "RichChat",
                $"Edited message {messageId} to: \"{newContent}\"."));

            return true;
        }
        catch (Exception ex)
        {
            _logSink.Log(new SimulatorLogEntry(
                DateTimeOffset.UtcNow,
                LogLevel.Error,
                author.DisplayName,
                author.Id,
                "RichChat",
                $"Failed to edit message: {ex.Message}",
                ex.ToString()));

            _toastService.ShowToast("Edit Exception", ex.Message, ToastSeverity.Error);
            return false;
        }
    }

    public async Task<bool> DeleteMessageAsync(
        SimulatedNodeId requestedByNodeId,
        SimulatedNodeId peerNodeId,
        Guid messageId,
        CancellationToken ct = default)
    {
        var requestedBy = _worldState.GetPersonaHost(requestedByNodeId);
        var peer = _worldState.GetPersonaHost(peerNodeId);
        if (requestedBy == null || peer == null) return false;

        try
        {
            var channelId = ChannelIdUtility.DeterministicFor(requestedBy.PublicIdentityId, peer.PublicIdentityId);
            using var scope = requestedBy.Services.CreateScope();
            var chatService = scope.ServiceProvider.GetRequiredService<IChatService>();

            var route = DeliveryRoute.DirectP2P;
            var routingRepo = scope.ServiceProvider.GetRequiredService<IPeerMailboxRoutingRepository>();
            if (peer.HomeRelay != null)
            {
                var now = scope.ServiceProvider.GetRequiredService<IDateTimeProvider>().UtcNow;
                var bestRoute = await routingRepo.GetBestRouteAsync(peer.PublicIdentityId, now, ct);
                if (bestRoute != null)
                {
                    route = DeliveryRoute.RelayedOneToOne;
                }
            }

            var command = new DeleteMessageCommand(
                TargetPayloadId: PayloadId.FromGuid(messageId),
                ChannelId: channelId,
                RequestedBy: requestedBy.PublicIdentityId,
                RecipientIdentityId: peer.PublicIdentityId,
                RecipientDeviceId: DeviceId.Primary,
                Route: route);

            var result = await chatService.DeleteMessageAsync(command, ct);
            if (!result.IsSuccess)
            {
                _toastService.ShowToast("Delete Error", result.Error.Description, ToastSeverity.Error);
                return false;
            }

            _stateBus.Publish(new SimulatorStateSignal(
                requestedBy.Id,
                SignalCategory.ChatTranscriptChanged,
                Details: peer.PublicIdentityId.ToString(),
                Payload: new ChatTranscriptSignalPayload(peer.PublicIdentityId)));

            var flushResult = await requestedBy.FlushOutboxAsync(ct);
            if (!flushResult.IsSuccess)
            {
                _toastService.ShowToast("Delete Delivery Deferred", flushResult.Error.Description, ToastSeverity.Warning);
                return false;
            }

            if (route == DeliveryRoute.RelayedOneToOne && peer.HomeRelay != null)
            {
                await peer.DrainMailboxAsync(ct);
            }

            _logSink.Log(new SimulatorLogEntry(
                DateTimeOffset.UtcNow,
                LogLevel.Information,
                requestedBy.DisplayName,
                requestedBy.Id,
                "RichChat",
                $"Deleted message {messageId} (tombstoned)."));

            return true;
        }
        catch (Exception ex)
        {
            _logSink.Log(new SimulatorLogEntry(
                DateTimeOffset.UtcNow,
                LogLevel.Error,
                requestedBy.DisplayName,
                requestedBy.Id,
                "RichChat",
                $"Failed to delete message: {ex.Message}",
                ex.ToString()));

            _toastService.ShowToast("Delete Exception", ex.Message, ToastSeverity.Error);
            return false;
        }
    }
}
