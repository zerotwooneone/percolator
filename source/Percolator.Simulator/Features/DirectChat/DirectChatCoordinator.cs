using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Percolator.Application.Ports;
using Percolator.Application.Routing;
using Percolator.Apps.Chat.Features.Messaging;
using Percolator.Domain.Channels.Model;
using Percolator.Domain.Channels.Ports;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Common;
using Percolator.Domain.Delivery.Ports;
using Percolator.Domain.Identities;
using Percolator.Domain.Identities.Ports;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.PluginSdk;
using Percolator.PluginSdk.Profiles;
using Percolator.Simulator.Features.Logging;
using Percolator.Simulator.Features.Shell;
using Percolator.Simulator.Features.Topology;

namespace Percolator.Simulator.Features.DirectChat;

internal sealed class DirectChatCoordinator
{
    private readonly ISimulatedWorldStateService _worldState;
    private readonly ISimulatorStateBus _stateBus;
    private readonly IStatusToastService _toastService;
    private readonly ISimulatorLogSink _logSink;

    public DirectChatCoordinator(
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

    public Task<bool> SendDirectMessageAsync(
        SimulatedNodeId senderNodeId,
        SimulatedNodeId recipientNodeId,
        string text,
        int? expireAfterSeconds = null,
        CancellationToken ct = default)
    {
        var recipient = _worldState.GetPersonaHost(recipientNodeId);
        if (recipient == null)
        {
            _toastService.ShowToast("Send Failed", "Recipient must be an active persona.", ToastSeverity.Error);
            return Task.FromResult(false);
        }
        return SendDirectMessageAsync(senderNodeId, recipient.PublicIdentityId, text, expireAfterSeconds, ct);
    }

    public Task<bool> SendDirectMessageAsync(
        SimulatedNodeId senderNodeId,
        PublicIdentityId recipientIdentityId,
        string text,
        int? expireAfterSeconds = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return Task.FromResult(false);

        return Task.Run(async () =>
        {
            var sender = _worldState.GetPersonaHost(senderNodeId);
            if (sender == null)
            {
                _toastService.ShowToast("Send Failed", "Sender must be an active persona.", ToastSeverity.Error);
                return false;
            }

            try
            {
                var channelId = ChannelId.DeterministicForDirect(sender.PublicIdentityId, recipientIdentityId);

                using var scope = sender.Services.CreateScope();
                var chatService = scope.ServiceProvider.GetRequiredService<IChatService>();

                // Auto-detect whether to route via DirectP2P or RelayedOneToOne based on IRoutingCoordinator
                var routingCoordinator = scope.ServiceProvider.GetRequiredService<IRoutingCoordinator>();
                var routeResult = await routingCoordinator.ResolveRouteAsync(recipientIdentityId, ct).ConfigureAwait(false);
                var route = (routeResult.IsSuccess && routeResult.Value.Mode == DeliveryRoutingMode.RelayMailbox)
                    ? DeliveryRoute.RelayedOneToOne
                    : DeliveryRoute.DirectP2P;

                var command = new SendMessageCommand(
                    ChannelId: channelId,
                    AuthorId: sender.PublicIdentityId,
                    RecipientIdentityId: recipientIdentityId,
                    RecipientDeviceId: DeviceId.Primary,
                    Content: text,
                    Mentions: null,
                    ReplyContext: null,
                    Attachments: null,
                    ExpireAfterSeconds: expireAfterSeconds,
                    LinkPreview: null,
                    Route: route);

                var result = await chatService.SendMessageAsync(command, ct);
                if (!result.IsSuccess)
                {
                    _toastService.ShowToast("Send Error", result.Error.Description, ToastSeverity.Error);
                    return false;
                }

                _stateBus.Publish(new SimulatorStateSignal(
                    sender.Id,
                    SignalCategory.ChatTranscriptChanged,
                    Details: recipientIdentityId.ToString(),
                    Payload: new ChatTranscriptSignalPayload(recipientIdentityId)));

                var recipientHost = _worldState.FindPersonaHostByIdentity(recipientIdentityId);
                if (recipientHost != null && route == DeliveryRoute.RelayedOneToOne && recipientHost.HomeRelay != null)
                {
                    await recipientHost.DrainMailboxAsync(ct);
                }

                var recipientDisplayName = recipientHost?.DisplayName ?? recipientIdentityId.ToString()[..8];
                _logSink.Log(new SimulatorLogEntry(
                    DateTimeOffset.UtcNow,
                    LogLevel.Information,
                    sender.DisplayName,
                    sender.Id,
                    "DirectChat",
                    $"Message ({route}) sent to '{recipientDisplayName}': \"{text}\""));

                return true;
            }
            catch (Exception ex)
            {
                _logSink.Log(new SimulatorLogEntry(
                    DateTimeOffset.UtcNow,
                    LogLevel.Error,
                    sender.DisplayName,
                    sender.Id,
                    "DirectChat",
                    $"Failed to send message: {ex.Message}",
                    ex.ToString()));

                _toastService.ShowToast("Send Exception", ex.Message, ToastSeverity.Error);
                return false;
            }
        }, ct);
    }

    public Task<IReadOnlyList<ChatMessageItemViewModel>> GetTranscriptAsync(
        SimulatedNodeId nodeId,
        SimulatedNodeId? peerNodeId = null,
        CancellationToken ct = default)
    {
        if (peerNodeId.HasValue)
        {
            var peerHost = _worldState.GetPersonaHost(peerNodeId.Value);
            if (peerHost != null)
            {
                return GetTranscriptAsync(nodeId, peerHost.PublicIdentityId, ct);
            }
        }
        return Task.FromResult<IReadOnlyList<ChatMessageItemViewModel>>(Array.Empty<ChatMessageItemViewModel>());
    }

    public Task<IReadOnlyList<ChatMessageItemViewModel>> GetTranscriptAsync(
        SimulatedNodeId nodeId,
        PublicIdentityId peerIdentityId,
        CancellationToken ct = default)
    {
        return Task.Run<IReadOnlyList<ChatMessageItemViewModel>>(async () =>
        {
            var host = _worldState.GetPersonaHost(nodeId);
            if (host == null) return Array.Empty<ChatMessageItemViewModel>();

            try
            {
                using var scope = host.Services.CreateScope();
                var chatQuery = scope.ServiceProvider.GetRequiredService<ISimulatorChatQueryService>();
                var contactRepo = scope.ServiceProvider.GetRequiredService<IPeerContactRepository>();
                var profileRepo = scope.ServiceProvider.GetRequiredService<IPeerProfileRepository>();

                var channelId = ChannelId.DeterministicForDirect(host.PublicIdentityId, peerIdentityId);
                var messages = await chatQuery.GetTranscriptAsync(channelId, ct);

                var peerHost = _worldState.FindPersonaHostByIdentity(peerIdentityId);

                // Resolve remote peer display name from local observer store (contact nickname first, then synced profile, then fallback)
                var contact = await contactRepo.GetByPeerIdAsync(host.PublicIdentityId, peerIdentityId, ct);
                var profile = await profileRepo.GetByPeerIdAsync(peerIdentityId, ct);

                var isExplicitNickname = contact != null &&
                    !string.IsNullOrWhiteSpace(contact.Nickname.Value) &&
                    contact.Nickname.Value != peerIdentityId.ToString();

                var peerDisplayName = isExplicitNickname
                    ? contact!.Nickname.Value
                    : (profile != null && !string.IsNullOrWhiteSpace(profile.DisplayName)
                        ? profile.DisplayName
                        : (peerHost?.DisplayName ?? peerIdentityId.ToString()[..8]));

                return messages.Select(m =>
                {
                    var isOutgoing = m.AuthorId == host.PublicIdentityId;
                    var senderName = isOutgoing ? host.DisplayName : peerDisplayName;
                    var timeText = DateTimeOffset.FromUnixTimeMilliseconds((long)m.TimestampUnixMs).ToLocalTime().ToString("HH:mm:ss");
                    var isRead = m.ReadAtUnixMs.HasValue;
                    var isDelivered = m.DeliveredAtUnixMs.HasValue || isRead;
                    var statusText = isOutgoing ? (isDelivered ? "✓✓" : "✓") : string.Empty;
                    var reactionsSummary = m.Reactions.Count > 0
                        ? string.Join(" ", m.Reactions.GroupBy(r => r).Select(g => $"{g.Key} {g.Count()}"))
                        : string.Empty;

                    return new ChatMessageItemViewModel
                    {
                        MessageId = m.MessageId.ToString(),
                        SenderName = senderName,
                        Content = m.Content,
                        TimestampText = timeText,
                        IsOutgoing = isOutgoing,
                        StatusText = statusText,
                        IsEdited = m.EditedAtUnixMs.HasValue,
                        IsDelivered = isDelivered,
                        IsRead = isRead,
                        ReactionsSummary = reactionsSummary
                    };
                }).ToList();
            }
            catch (Exception ex)
            {
                _logSink.Log(new SimulatorLogEntry(
                    DateTimeOffset.UtcNow,
                    LogLevel.Warning,
                    host.DisplayName,
                    host.Id,
                    "DirectChat",
                    $"Error fetching chat transcript: {ex.Message}"));

                return Array.Empty<ChatMessageItemViewModel>();
            }
        }, ct);
    }

    public Task<bool> MarkConversationAsReadAsync(
        SimulatedNodeId readerNodeId,
        SimulatedNodeId senderNodeId,
        Guid upToMessageId,
        CancellationToken ct = default)
    {
        var sender = _worldState.GetPersonaHost(senderNodeId);
        if (sender == null) return Task.FromResult(false);
        return MarkConversationAsReadAsync(readerNodeId, sender.PublicIdentityId, upToMessageId, ct);
    }

    public Task<bool> MarkConversationAsReadAsync(
        SimulatedNodeId readerNodeId,
        PublicIdentityId senderIdentityId,
        Guid upToMessageId,
        CancellationToken ct = default)
    {
        return Task.Run(async () =>
        {
            var reader = _worldState.GetPersonaHost(readerNodeId);
            if (reader == null) return false;

            try
            {
                var channelId = ChannelId.DeterministicForDirect(reader.PublicIdentityId, senderIdentityId);
                using var scope = reader.Services.CreateScope();
                var chatService = scope.ServiceProvider.GetRequiredService<IChatService>();

                var routingCoordinator = scope.ServiceProvider.GetRequiredService<IRoutingCoordinator>();
                var routeResult = await routingCoordinator.ResolveRouteAsync(senderIdentityId, ct).ConfigureAwait(false);
                var route = (routeResult.IsSuccess && routeResult.Value.Mode == DeliveryRoutingMode.RelayMailbox)
                    ? DeliveryRoute.RelayedOneToOne
                    : DeliveryRoute.DirectP2P;

                var command = new MarkAsReadCommand(
                    ChannelId: channelId,
                    ReaderId: reader.PublicIdentityId,
                    UpToPayloadId: PayloadId.FromGuid(upToMessageId),
                    RecipientIdentityId: senderIdentityId,
                    RecipientDeviceId: DeviceId.Primary,
                    Route: route);

                var result = await chatService.MarkAsReadAsync(command, ct);
                if (!result.IsSuccess) return false;

                var senderHost = _worldState.FindPersonaHostByIdentity(senderIdentityId);
                if (route == DeliveryRoute.RelayedOneToOne && senderHost?.HomeRelay != null)
                {
                    await senderHost.DrainMailboxAsync(ct);
                }

                _stateBus.Publish(new SimulatorStateSignal(
                    reader.Id,
                    SignalCategory.ChatTranscriptChanged,
                    Details: senderIdentityId.ToString(),
                    Payload: new ChatTranscriptSignalPayload(senderIdentityId)));

                return true;
            }
            catch (Exception ex)
            {
                _logSink.Log(new SimulatorLogEntry(
                    DateTimeOffset.UtcNow,
                    LogLevel.Warning,
                    reader.DisplayName,
                    reader.Id,
                    "DirectChat",
                    $"Error sending read receipt: {ex.Message}"));

                return false;
            }
        }, ct);
    }
}
