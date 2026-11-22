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
using Percolator.Application.Channels.Events;
using Percolator.Application.Ports;
using Percolator.Desktop.Common;
using Percolator.Desktop.Common.Services;
using Percolator.Desktop.Features.Chats.Models;
using Percolator.Desktop.Features.Transports.Models;
using Percolator.Desktop.Features.Transports.Services;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Identities;
using Percolator.Domain.Identities.Model;
using Percolator.PluginSdk.Events;
using R3;

/// <summary>
/// Free-threaded presentation state service managing active persona conversations,
/// direct peer-to-peer chats, relayed conversations, and staged outbound pending connections ([UISS-001], [UISS-005]).
/// </summary>
public sealed class ConversationStateService : IConversationStateService
{
    private static readonly TimeSpan CoalesceWindow = TimeSpan.FromMilliseconds(50);

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
    private readonly Subject<PublicIdentityId> _reloadRequests = new();
    private readonly object _lock = new();
    private readonly object _triggerLock = new();
    private readonly CancellationTokenSource _disposeCts = new();

    private CancellationTokenSource _personaCts = new();
    private long _loadGeneration;
    private DisposableBag _bag;
    private PublicIdentityId? _activePersonaId;
    private volatile bool _disposed;

    public ConversationStateService(
        IConversationListQueryService conversationQueryService,
        IPeerContactQueryService contactQueryService,
        IOutboundPendingRetryWorker retryWorker,
        ICurrentPersonaService currentPersonaService,
        IEventSubscriber eventSubscriber,
        ILogger<ConversationStateService> logger,
        TimeProvider timeProvider)
    {
        _conversationQueryService = conversationQueryService ?? throw new ArgumentNullException(nameof(conversationQueryService));
        _contactQueryService = contactQueryService ?? throw new ArgumentNullException(nameof(contactQueryService));
        _retryWorker = retryWorker ?? throw new ArgumentNullException(nameof(retryWorker));
        _currentPersonaService = currentPersonaService ?? throw new ArgumentNullException(nameof(currentPersonaService));
        ArgumentNullException.ThrowIfNull(eventSubscriber);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

        _conversationsView = _conversationsList.CreateView(x => x);

        _reloadRequests
            .ThrottleFirstLast(CoalesceWindow, _timeProvider)
            .SubscribeAwait(async (personaId, ct) =>
            {
                await LoadConversationsInternalAsync(personaId, requireAlreadyActive: true, ct).ConfigureAwait(false);
            }, AwaitOperation.Switch)
            .AddTo(ref _bag);

        eventSubscriber.ObserveEvent<DirectChannelCreatedEvent>()
            .Subscribe(evt =>
            {
                PublicIdentityId? activePersona;
                lock (_lock)
                {
                    if (_disposed) return;
                    activePersona = _activePersonaId;
                }

                if (activePersona.HasValue && evt.OwnerIdentityId == activePersona.Value)
                {
                    TriggerReload(activePersona.Value);
                }
            })
            .AddTo(ref _bag);

        _retryWorker.ConnectionSucceeded
            .Subscribe(model =>
            {
                lock (_lock)
                {
                    var match = _conversationsList.FirstOrDefault(c => c.PeerOrGroupId == model.PeerId && c.IsPendingOutbound);
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
            _ = LoadConversationsInternalAsync(initialPersona.IdentityId, requireAlreadyActive: false, _disposeCts.Token);
        }

        _currentPersonaService.CurrentPersonaChanged
            .SubscribeAwait(async (persona, ct) =>
            {
                if (persona != null && persona.IdentityId.IsValid)
                {
                    await LoadConversationsInternalAsync(persona.IdentityId, requireAlreadyActive: false, ct).ConfigureAwait(false);
                }
                else
                {
                    CancelAndClearActivePersona();
                }
            }, AwaitOperation.Switch)
            .AddTo(ref _bag);
    }

    public ISynchronizedView<ConversationSummaryModel, ConversationSummaryModel> Conversations => _conversationsView;

    public Observable<OutboundPendingConnectionModel> ConnectionSucceeded => _connectionSucceeded;

    public Observable<PublicIdentityId> ConnectionCancelled => _connectionCancelled;

    public Task LoadConversationsForPersonaAsync(PublicIdentityId personaId, CancellationToken ct = default)
        => LoadConversationsInternalAsync(personaId, requireAlreadyActive: false, ct);

    private async Task LoadConversationsInternalAsync(
        PublicIdentityId personaId,
        bool requireAlreadyActive,
        CancellationToken ct)
    {
        if (_disposed || !personaId.IsValid) return;

        CancellationTokenSource? oldCts = null;
        CancellationTokenSource linkedCts;
        long generation;

        lock (_lock)
        {
            if (_disposed) return;

            if (requireAlreadyActive && (!_activePersonaId.HasValue || _activePersonaId.Value != personaId))
            {
                return;
            }

            if (_activePersonaId.HasValue && _activePersonaId.Value != personaId)
            {
                _conversationsList.Clear();
                oldCts = _personaCts;
                _personaCts = new CancellationTokenSource();
            }

            _activePersonaId = personaId;
            generation = ++_loadGeneration;
            linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _disposeCts.Token, _personaCts.Token);
        }

        if (oldCts != null)
        {
            oldCts.Cancel();
            oldCts.Dispose();
        }

        using (linkedCts)
        {
            if (linkedCts.IsCancellationRequested) return;

            _logger.LogInformation("Loading conversations for persona {PersonaId}", personaId);

            try
            {
                var nowUnixMs = (ulong)_timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
                var conversations = await _conversationQueryService.GetConversationsAsync(personaId, nowUnixMs, linkedCts.Token).ConfigureAwait(false);
                var contacts = await _contactQueryService.GetContactSummariesAsync(personaId, linkedCts.Token).ConfigureAwait(false);

                lock (_lock)
                {
                    if (_disposed || linkedCts.IsCancellationRequested || _loadGeneration != generation || _activePersonaId != personaId)
                    {
                        return;
                    }

                    // Preserve existing pending outbound items that haven't established connection yet
                    var pendingOutbound = _conversationsList.Where(c => c.IsPendingOutbound).ToList();

                    _conversationsList.Clear();

                    // Add loaded conversations from chat service
                    foreach (var conv in conversations)
                    {
                        var peerId = conv.RemotePeerId ?? (conv.Kind == ConversationChannelKind.Direct ? conv.LastAuthorId ?? personaId : new PublicIdentityId(conv.ChannelId.Value));
                        var routeType = conv.RelayIdentityId != null ? ConversationRouteType.Relayed : ConversationRouteType.DirectP2P;
                        _conversationsList.Add(new ConversationSummaryModel(
                            conv.ChannelId,
                            peerId,
                            conv.Title,
                            routeType,
                            relayId: conv.RelayIdentityId,
                            lastMessageSnippet: conv.LastMessageSnippet,
                            lastActivityUtc: DateTimeOffset.FromUnixTimeMilliseconds((long)conv.LastActivityTimestampUnixMs),
                            unreadCount: conv.UnreadCount,
                            isPendingOutbound: false,
                            deliveryStatus: conv.IsPendingAcceptance ? ConversationDeliveryStatus.Pending : ConversationDeliveryStatus.Connected));
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
            catch (OperationCanceledException)
            {
                // Expected when superseded by AwaitOperation.Switch, persona switch, or disposal
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load conversations for persona {PersonaId}", personaId);
            }
        }
    }

    private void TriggerReload(PublicIdentityId personaId)
    {
        lock (_triggerLock)
        {
            if (_disposed) return;
            _reloadRequests.OnNext(personaId);
        }
    }

    private void CancelAndClearActivePersona()
    {
        CancellationTokenSource oldCts;
        lock (_lock)
        {
            if (_disposed) return;
            _activePersonaId = null;
            _loadGeneration++;
            _conversationsList.Clear();
            oldCts = _personaCts;
            _personaCts = new CancellationTokenSource();
        }

        oldCts.Cancel();
        oldCts.Dispose();
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
        CancellationTokenSource personaCtsToDispose;
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            _activePersonaId = null;
            _loadGeneration++;
            personaCtsToDispose = _personaCts;
        }

        _disposeCts.Cancel();
        personaCtsToDispose.Cancel();

        _bag.Dispose();

        lock (_triggerLock)
        {
            _reloadRequests.OnCompleted();
            _reloadRequests.Dispose();
        }

        _conversationsView.Dispose();

        _connectionSucceeded.OnCompleted();
        _connectionSucceeded.Dispose();

        _connectionCancelled.OnCompleted();
        _connectionCancelled.Dispose();

        personaCtsToDispose.Dispose();
        _disposeCts.Dispose();
    }
}
