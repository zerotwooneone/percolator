namespace Percolator.Simulator.Features.RelayedChat;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Percolator.Application.Connections;
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
using Percolator.Simulator.Features.DirectChat;
using Percolator.Simulator.Features.Logging;
using Percolator.Simulator.Features.Shell;
using Percolator.Simulator.Features.Topology;

internal sealed class RelayChatCoordinator
{
    private readonly ISimulatedWorldStateService _worldState;
    private readonly ISimulatorStateBus _stateBus;
    private readonly IStatusToastService _toastService;
    private readonly ISimulatorLogSink _logSink;

    public RelayChatCoordinator(
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
                    Route: DeliveryRoute.RelayedOneToOne);

                var result = await chatService.SendMessageAsync(command, ct).ConfigureAwait(false);
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
                var recipientDisplayName = recipientHost?.DisplayName ?? recipientIdentityId.ToString()[..8];
                _logSink.Log(new SimulatorLogEntry(
                    DateTimeOffset.UtcNow,
                    LogLevel.Information,
                    sender.DisplayName,
                    sender.Id,
                    "RelayedChat",
                    $"Message sent to '{recipientDisplayName}': \"{text}\""));

                return true;
            }
            catch (Exception ex)
            {
                _logSink.Log(new SimulatorLogEntry(
                    DateTimeOffset.UtcNow,
                    LogLevel.Error,
                    sender.DisplayName,
                    sender.Id,
                    "RelayedChat",
                    $"SendDirectMessage exception: {ex.Message}",
                    ex.ToString()));
                _toastService.ShowToast("Send Exception", ex.Message, ToastSeverity.Error);
                return false;
            }
        }, ct);
    }

    public async Task<IReadOnlyList<ChatMessageItemViewModel>> GetTranscriptAsync(
        SimulatedNodeId nodeId,
        SimulatedNodeId peerNodeId,
        CancellationToken ct = default)
    {
        var peer = _worldState.GetPersonaHost(peerNodeId);
        if (peer == null) return Array.Empty<ChatMessageItemViewModel>();
        return await GetTranscriptAsync(nodeId, peer.PublicIdentityId, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ChatMessageItemViewModel>> GetTranscriptAsync(
        SimulatedNodeId nodeId,
        PublicIdentityId peerIdentityId,
        CancellationToken ct = default)
    {
        var host = _worldState.GetPersonaHost(nodeId);
        if (host == null) return Array.Empty<ChatMessageItemViewModel>();

        var channelId = ChannelId.DeterministicForDirect(host.PublicIdentityId, peerIdentityId);
        using var scope = host.Services.CreateScope();
        var query = scope.ServiceProvider.GetRequiredService<ISimulatorChatQueryService>();
        var readModels = await query.GetTranscriptAsync(channelId, ct).ConfigureAwait(false);

        var viewModels = new List<ChatMessageItemViewModel>(readModels.Count);
        foreach (var rm in readModels)
        {
            var isOutgoing = rm.AuthorId == host.PublicIdentityId;
            var senderName = isOutgoing ? "You" : (_worldState.FindPersonaHostByIdentity(rm.AuthorId)?.DisplayName ?? rm.AuthorId.ToString()[..8]);
            var timeText = DateTimeOffset.FromUnixTimeMilliseconds((long)rm.TimestampUnixMs).ToLocalTime().ToString("HH:mm:ss");

            viewModels.Add(new ChatMessageItemViewModel
            {
                MessageId = rm.MessageId.ToString(),
                SenderName = senderName,
                Content = rm.Content,
                TimestampText = timeText,
                IsOutgoing = isOutgoing,
                IsEdited = rm.EditedAtUnixMs.HasValue,
                IsDelivered = rm.DeliveredAtUnixMs.HasValue,
                IsRead = rm.ReadAtUnixMs.HasValue,
                ReactionsSummary = rm.Reactions.Count > 0 ? string.Join(" ", rm.Reactions) : string.Empty
            });
        }

        return viewModels;
    }

    public async Task<bool> MarkConversationAsReadAsync(
        SimulatedNodeId ownerNodeId,
        SimulatedNodeId peerNodeId,
        Guid messageId,
        CancellationToken ct = default)
    {
        var peer = _worldState.GetPersonaHost(peerNodeId);
        if (peer == null) return false;
        return await MarkConversationAsReadAsync(ownerNodeId, peer.PublicIdentityId, messageId, ct).ConfigureAwait(false);
    }

    public async Task<bool> MarkConversationAsReadAsync(
        SimulatedNodeId ownerNodeId,
        PublicIdentityId peerIdentityId,
        Guid messageId,
        CancellationToken ct = default)
    {
        var owner = _worldState.GetPersonaHost(ownerNodeId);
        if (owner == null) return false;

        using var scope = owner.Services.CreateScope();
        var chatService = scope.ServiceProvider.GetRequiredService<IChatService>();
        var channelId = ChannelId.DeterministicForDirect(owner.PublicIdentityId, peerIdentityId);

        var command = new MarkAsReadCommand(
            ChannelId: channelId,
            ReaderId: owner.PublicIdentityId,
            UpToPayloadId: PayloadId.FromGuid(messageId),
            RecipientIdentityId: peerIdentityId,
            RecipientDeviceId: DeviceId.Primary,
            Route: DeliveryRoute.RelayedOneToOne);

        var result = await chatService.MarkAsReadAsync(command, ct).ConfigureAwait(false);

        return result.IsSuccess;
    }

    public async Task<bool> AcceptHandshakeAsync(
        SimulatedNodeId ownerNodeId,
        PublicIdentityId senderId,
        CancellationToken ct = default)
    {
        var owner = _worldState.GetPersonaHost(ownerNodeId);
        if (owner == null) return false;

        using var scope = owner.Services.CreateScope();
        var workflow = scope.ServiceProvider.GetRequiredService<IInboundConnectionWorkflow>();
        var result = await workflow.AcceptRequestAsync(owner.PublicIdentityId, senderId, ct).ConfigureAwait(false);

        if (result.IsSuccess)
        {
            _toastService.ShowToast("Contact Approved", $"Contact request from {senderId.Value.ToString()[..8]} approved.", ToastSeverity.Success);
            _stateBus.Publish(new SimulatorStateSignal(owner.Id, SignalCategory.PendingHandshakeChanged));
            return true;
        }

        _toastService.ShowToast("Approval Failed", result.Error.Description, ToastSeverity.Error);
        return false;
    }

    public async Task<bool> RejectHandshakeAsync(
        SimulatedNodeId ownerNodeId,
        PublicIdentityId senderId,
        bool block = false,
        CancellationToken ct = default)
    {
        var owner = _worldState.GetPersonaHost(ownerNodeId);
        if (owner == null) return false;

        using var scope = owner.Services.CreateScope();
        var workflow = scope.ServiceProvider.GetRequiredService<IInboundConnectionWorkflow>();

        var result = block
            ? await workflow.BlockRequestAsync(owner.PublicIdentityId, senderId, ct).ConfigureAwait(false)
            : await workflow.RejectRequestAsync(owner.PublicIdentityId, senderId, ct).ConfigureAwait(false);

        if (result.IsSuccess)
        {
            _toastService.ShowToast(block ? "Contact Blocked" : "Contact Rejected", $"Contact request from {senderId.Value.ToString()[..8]} {(block ? "blocked" : "rejected")}.", ToastSeverity.Informational);
            _stateBus.Publish(new SimulatorStateSignal(owner.Id, SignalCategory.PendingHandshakeChanged));
            return true;
        }

        _toastService.ShowToast("Rejection Failed", result.Error.Description, ToastSeverity.Error);
        return false;
    }
}
