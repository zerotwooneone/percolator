// SPECIFICATION GOVERNANCE: Read Percolator.Desktop/spec/UiStateService.spec.md before modifying this file.

namespace Percolator.Desktop.Features.Chats.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using ObservableCollections;
using Percolator.Apps.Chat.Features.Messaging;
using Percolator.Application.Ports;
using Percolator.Desktop.Common.Services;
using Percolator.Desktop.Features.Chats.Models;
using Percolator.Desktop.Features.Transports.Models;
using Percolator.Desktop.Features.Transports.Services;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Identities;
using Percolator.Domain.Identities.Model;
using R3;

/// <summary>
/// Free-threaded presentation state service managing active persona conversations,
/// direct peer-to-peer chats, relayed conversations, and staged outbound pending connections ([UISS-001], [UISS-005]).
/// </summary>
public sealed class ConversationStateService : IConversationStateService
{
    private readonly IConversationListQueryService _conversationQueryService;
    private readonly IPeerContactQueryService _contactQueryService;
    private readonly IOutboundPendingRetryWorker _retryWorker;
    private readonly ICurrentPersonaService _currentPersonaService;
    private readonly ILogger<ConversationStateService> _logger;
    private readonly TimeProvider _timeProvider;

    private readonly ObservableList<ConversationSummaryModel> _conversationsList = new();
    private readonly ISynchronizedView<ConversationSummaryModel, ConversationSummaryModel> _conversationsView;
    private readonly Subject<OutboundPendingConnectionModel> _connectionSucceeded = new();
    private readonly Subject<PublicIdentityId> _connectionCancelled = new();
    private readonly object _lock = new();

    private DisposableBag _bag;
    private PublicIdentityId? _activePersonaId;
    private bool _disposed;

    public ConversationStateService(
        IConversationListQueryService conversationQueryService,
        IPeerContactQueryService contactQueryService,
        IOutboundPendingRetryWorker retryWorker,
        ICurrentPersonaService currentPersonaService,
        ILogger<ConversationStateService> logger,
        TimeProvider timeProvider)
    {
        _conversationQueryService = conversationQueryService ?? throw new ArgumentNullException(nameof(conversationQueryService));
        _contactQueryService = contactQueryService ?? throw new ArgumentNullException(nameof(contactQueryService));
        _retryWorker = retryWorker ?? throw new ArgumentNullException(nameof(retryWorker));
        _currentPersonaService = currentPersonaService ?? throw new ArgumentNullException(nameof(currentPersonaService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

        _conversationsView = _conversationsList.CreateView(x => x);

        _retryWorker.ConnectionSucceeded
            .Subscribe(model =>
            {
                ConversationSummaryModel? match = null;
                lock (_lock)
                {
                    match = _conversationsList.FirstOrDefault(c => c.PeerOrGroupId == model.PeerId && c.IsPendingOutbound);
                    if (match != null)
                    {
                        _logger.LogInformation("Transitioning pending conversation for peer {PeerId} to Connected", model.PeerId);
                        match.IsPendingOutbound = false;
                        match.DeliveryStatus = ConversationDeliveryStatus.Connected;
                        match.LastMessageSnippet = "Connected.";
                    }
                }

                _connectionSucceeded.OnNext(model);
            })
            .AddTo(ref _bag);

        if (_currentPersonaService.CurrentPersona is { } initialPersona && initialPersona.IdentityId.IsValid)
        {
            _activePersonaId = initialPersona.IdentityId;
            _ = LoadConversationsForPersonaAsync(initialPersona.IdentityId);
        }

        _currentPersonaService.CurrentPersonaChanged
            .Subscribe(persona =>
            {
                if (persona != null && persona.IdentityId.IsValid)
                {
                    _ = LoadConversationsForPersonaAsync(persona.IdentityId);
                }
                else
                {
                    lock (_lock)
                    {
                        _activePersonaId = null;
                        _conversationsList.Clear();
                    }
                }
            })
            .AddTo(ref _bag);
    }

    public ISynchronizedView<ConversationSummaryModel, ConversationSummaryModel> Conversations => _conversationsView;

    public Observable<OutboundPendingConnectionModel> ConnectionSucceeded => _connectionSucceeded;

    public Observable<PublicIdentityId> ConnectionCancelled => _connectionCancelled;

    public async Task LoadConversationsForPersonaAsync(PublicIdentityId personaId, CancellationToken ct = default)
    {
        if (_disposed || !personaId.IsValid) return;

        _logger.LogInformation("Loading conversations for persona {PersonaId}", personaId);

        try
        {
            var nowUnixMs = (ulong)_timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
            var conversations = await _conversationQueryService.GetConversationsAsync(personaId, nowUnixMs, ct).ConfigureAwait(false);
            var contacts = await _contactQueryService.GetContactSummariesAsync(personaId, ct).ConfigureAwait(false);

            lock (_lock)
            {
                _activePersonaId = personaId;

                // Preserve existing pending outbound items that haven't established connection yet
                var pendingOutbound = _conversationsList.Where(c => c.IsPendingOutbound).ToList();

                _conversationsList.Clear();

                // Add loaded conversations from chat service
                foreach (var conv in conversations)
                {
                    var peerId = conv.LastAuthorId ?? personaId;
                    _conversationsList.Add(new ConversationSummaryModel(
                        conv.ChannelId,
                        peerId,
                        conv.Title,
                        ConversationRouteType.DirectP2P,
                        relayId: null,
                        lastMessageSnippet: conv.LastMessageSnippet,
                        lastActivityUtc: DateTimeOffset.FromUnixTimeMilliseconds((long)conv.LastMessageTimestampUnixMs),
                        unreadCount: conv.UnreadCount,
                        isPendingOutbound: false,
                        deliveryStatus: ConversationDeliveryStatus.Connected));
                }

                // For established contacts that don't have a chat thread in conversations list yet, surface them as Direct P2P conversations
                foreach (var contact in contacts.Where(c => c.State == ContactState.Active))
                {
                    if (!_conversationsList.Any(c => c.PeerOrGroupId == contact.IdentityId))
                    {
                        _conversationsList.Add(new ConversationSummaryModel(
                            ChannelId.New(),
                            contact.IdentityId,
                            contact.DisplayName,
                            ConversationRouteType.DirectP2P,
                            relayId: null,
                            lastMessageSnippet: "Direct peer connection active.",
                            lastActivityUtc: contact.AddedAtUtc,
                            unreadCount: 0,
                            isPendingOutbound: false,
                            deliveryStatus: ConversationDeliveryStatus.Connected));
                    }
                }

                // Restore pending outbound items
                foreach (var pending in pendingOutbound)
                {
                    if (!_conversationsList.Any(c => c.PeerOrGroupId == pending.PeerOrGroupId))
                    {
                        _conversationsList.Add(pending);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load conversations for persona {PersonaId}", personaId);
        }
    }

    public void StagePendingOutbound(OutboundPendingConnectionModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (_disposed) return;

        lock (_lock)
        {
            var existing = _conversationsList.FirstOrDefault(c => c.PeerOrGroupId == model.PeerId);
            if (existing != null)
            {
                _logger.LogInformation("Peer {PeerId} is already in conversation directory; updating status to Pending", model.PeerId);
                existing.IsPendingOutbound = true;
                existing.DeliveryStatus = ConversationDeliveryStatus.Pending;
                existing.LastMessageSnippet = "Waiting for peer to come online...";
            }
            else
            {
                _logger.LogInformation("Staging offline peer {PeerId} ({DisplayName}) as pending outbound conversation", model.PeerId, model.DisplayName);
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

                _conversationsList.Add(summary);
            }
        }

        _retryWorker.EnqueueRetry(model);
    }

    public void CancelOutboundRequest(PublicIdentityId peerId)
    {
        if (_disposed) return;

        var removed = false;
        lock (_lock)
        {
            var existing = _conversationsList.FirstOrDefault(c => c.PeerOrGroupId == peerId && c.IsPendingOutbound);
            if (existing != null)
            {
                _logger.LogInformation("Removing pending outbound conversation for peer {PeerId} on cancellation", peerId);
                _conversationsList.Remove(existing);
                removed = true;
            }
        }

        if (removed || _retryWorker.IsRetrying(peerId))
        {
            _retryWorker.CancelRetry(peerId);
            _connectionCancelled.OnNext(peerId);
        }
    }

    public bool IsPending(PublicIdentityId peerId)
    {
        lock (_lock)
        {
            return _conversationsList.Any(c => c.PeerOrGroupId == peerId && c.IsPendingOutbound);
        }
    }

    public void RegisterDirectConversation(PublicIdentityId peerId, string displayName, ChannelId? channelId = null)
    {
        if (_disposed || !peerId.IsValid) return;

        lock (_lock)
        {
            var existing = _conversationsList.FirstOrDefault(c => c.PeerOrGroupId == peerId);
            if (existing != null)
            {
                existing.DisplayName = displayName;
                existing.IsPendingOutbound = false;
                existing.DeliveryStatus = ConversationDeliveryStatus.Connected;
            }
            else
            {
                _conversationsList.Add(new ConversationSummaryModel(
                    channelId ?? ChannelId.New(),
                    peerId,
                    displayName,
                    ConversationRouteType.DirectP2P,
                    relayId: null,
                    lastMessageSnippet: "Direct peer connection active.",
                    lastActivityUtc: _timeProvider.GetUtcNow(),
                    unreadCount: 0,
                    isPendingOutbound: false,
                    deliveryStatus: ConversationDeliveryStatus.Connected));
            }
        }
    }

    public void RegisterRelayedConversation(
        ChannelId channelId,
        string displayName,
        PublicIdentityId relayId,
        bool isGroup = false)
    {
        if (_disposed) return;

        lock (_lock)
        {
            var existing = _conversationsList.FirstOrDefault(c => c.ConversationId == channelId);
            if (existing != null)
            {
                existing.DisplayName = displayName;
            }
            else
            {
                _conversationsList.Add(new ConversationSummaryModel(
                    conversationId: channelId,
                    peerOrGroupId: relayId,
                    displayName: displayName,
                    routeType: ConversationRouteType.Relayed,
                    relayId: relayId,
                    lastMessageSnippet: isGroup ? "Group created." : "Relayed connection active.",
                    lastActivityUtc: _timeProvider.GetUtcNow(),
                    unreadCount: 0,
                    isPendingOutbound: false,
                    deliveryStatus: ConversationDeliveryStatus.Connected));
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _bag.Dispose();
        _conversationsView.Dispose();

        _connectionSucceeded.OnCompleted();
        _connectionSucceeded.Dispose();

        _connectionCancelled.OnCompleted();
        _connectionCancelled.Dispose();
    }
}
