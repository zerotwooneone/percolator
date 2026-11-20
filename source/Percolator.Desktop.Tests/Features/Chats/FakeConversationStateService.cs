namespace Percolator.Desktop.Tests.Features.Chats;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ObservableCollections;
using Percolator.Desktop.Features.Chats.Models;
using Percolator.Desktop.Features.Chats.Services;
using Percolator.Desktop.Features.Transports.Models;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Identities;
using R3;

internal sealed class FakeConversationStateService : IConversationStateService
{
    private readonly ObservableList<ConversationSummaryModel> _list = new();
    private readonly ISynchronizedView<ConversationSummaryModel, ConversationSummaryModel> _view;
    private readonly Subject<OutboundPendingConnectionModel> _connectionSucceeded = new();
    private readonly Subject<PublicIdentityId> _connectionCancelled = new();

    public FakeConversationStateService()
    {
        _view = _list.CreateView(x => x);
    }

    public ISynchronizedView<ConversationSummaryModel, ConversationSummaryModel> Conversations => _view;
    public ObservableList<ConversationSummaryModel> BackingList => _list;
    public Observable<OutboundPendingConnectionModel> ConnectionSucceeded => _connectionSucceeded;
    public Observable<PublicIdentityId> ConnectionCancelled => _connectionCancelled;

    public List<OutboundPendingConnectionModel> StagedPending { get; } = new();
    public List<(PublicIdentityId PeerId, string DisplayName, ChannelId? ChannelId)> RegisteredDirect { get; } = new();
    public List<PublicIdentityId> CancelledRequests { get; } = new();

    public Task LoadConversationsForPersonaAsync(PublicIdentityId personaId, CancellationToken ct = default)
    {
        return Task.CompletedTask;
    }

    public void StagePendingOutbound(OutboundPendingConnectionModel model)
    {
        StagedPending.Add(model);
        var summary = new ConversationSummaryModel(
            conversationId: null,
            peerOrGroupId: model.PeerId,
            displayName: model.DisplayName,
            routeType: ConversationRouteType.DirectP2P,
            relayId: null,
            lastMessageSnippet: "Waiting for peer to come online...",
            lastActivityUtc: model.InitiatedAtUtc,
            unreadCount: 0,
            isPendingOutbound: true,
            deliveryStatus: ConversationDeliveryStatus.Pending);
        _list.Add(summary);
    }

    public void CancelOutboundRequest(PublicIdentityId peerId)
    {
        CancelledRequests.Add(peerId);
        var existing = _list.FirstOrDefault(c => c.PeerOrGroupId == peerId);
        if (existing != null) _list.Remove(existing);
        _connectionCancelled.OnNext(peerId);
    }

    public bool IsPending(PublicIdentityId peerId)
    {
        return _list.Any(c => c.PeerOrGroupId == peerId && c.IsPendingOutbound);
    }

    public void RegisterDirectConversation(PublicIdentityId peerId, string displayName, ChannelId? channelId = null)
    {
        RegisteredDirect.Add((peerId, displayName, channelId));
        var existing = _list.FirstOrDefault(c => c.PeerOrGroupId == peerId);
        if (existing != null)
        {
            existing.DisplayName = displayName;
            existing.IsPendingOutbound = false;
            existing.DeliveryStatus = ConversationDeliveryStatus.Connected;
        }
        else
        {
            _list.Add(new ConversationSummaryModel(
                channelId ?? ChannelId.New(),
                peerId,
                displayName,
                ConversationRouteType.DirectP2P,
                relayId: null,
                lastMessageSnippet: "Direct peer connection active.",
                lastActivityUtc: DateTimeOffset.UtcNow,
                unreadCount: 0,
                isPendingOutbound: false,
                deliveryStatus: ConversationDeliveryStatus.Connected));
        }
    }

    public void SimulateConnectionSuccess(OutboundPendingConnectionModel model)
    {
        var match = _list.FirstOrDefault(c => c.PeerOrGroupId == model.PeerId);
        if (match != null)
        {
            match.IsPendingOutbound = false;
            match.DeliveryStatus = ConversationDeliveryStatus.Connected;
        }
        _connectionSucceeded.OnNext(model);
    }

    public void Dispose()
    {
        _view.Dispose();
        _connectionSucceeded.OnCompleted();
        _connectionSucceeded.Dispose();
        _connectionCancelled.OnCompleted();
        _connectionCancelled.Dispose();
    }
}
