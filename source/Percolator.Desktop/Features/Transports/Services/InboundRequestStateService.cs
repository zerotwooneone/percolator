// SPECIFICATION GOVERNANCE: Read Percolator.Desktop/spec/UiStateService.spec.md before modifying this file.

namespace Percolator.Desktop.Features.Transports.Services;

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using ObservableCollections;
using Percolator.Application.Connections;
using Percolator.Application.Ports;
using Percolator.Application.Profiles.Events;
using Percolator.Desktop.Common;
using Percolator.Desktop.Common.Services;
using Percolator.Desktop.Features.Transports.Models;
using Percolator.Domain.Identities;
using Percolator.Domain.Identities.Model;
using Percolator.PluginSdk.Events;
using R3;

/// <summary>
/// Free-threaded UI state service managing the quarantined inbound contact requests queue
/// and driving the Left Rail indicator light ([UISS-001], [UISS-002], [DEC-PULL-BASED-CONSENT-STAGING]).
/// </summary>
public sealed class InboundRequestStateService : IInboundRequestStateService
{
    private static readonly TimeSpan CoalesceWindow = TimeSpan.FromMilliseconds(50);

    private readonly IPeerContactQueryService _contactQueryService;
    private readonly IInboundConnectionWorkflow _inboundWorkflow;
    private readonly ICurrentPersonaService _currentPersonaService;
    private readonly ILogger<InboundRequestStateService> _logger;
    private readonly TimeProvider _timeProvider;

    private readonly ObservableList<InboundPendingRequestModel> _requestsList = new();
    private readonly ISynchronizedView<InboundPendingRequestModel, InboundPendingRequestModel> _requestsView;
    private readonly ReactiveProperty<int> _pendingCount = new(0);
    private readonly Subject<PublicIdentityId> _reloadRequests = new();
    private readonly object _lock = new();
    private readonly object _triggerLock = new();
    private readonly CancellationTokenSource _disposeCts = new();

    private CancellationTokenSource _personaCts = new();
    private long _loadGeneration;
    private DisposableBag _bag;
    private PublicIdentityId? _activePersonaId;
    private volatile bool _disposed;

    public InboundRequestStateService(
        IPeerContactQueryService contactQueryService,
        IInboundConnectionWorkflow inboundWorkflow,
        ICurrentPersonaService currentPersonaService,
        IEventSubscriber eventSubscriber,
        ILogger<InboundRequestStateService> logger,
        TimeProvider timeProvider)
    {
        _contactQueryService = contactQueryService ?? throw new ArgumentNullException(nameof(contactQueryService));
        _inboundWorkflow = inboundWorkflow ?? throw new ArgumentNullException(nameof(inboundWorkflow));
        _currentPersonaService = currentPersonaService ?? throw new ArgumentNullException(nameof(currentPersonaService));
        ArgumentNullException.ThrowIfNull(eventSubscriber);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

        _requestsView = _requestsList.CreateView(x => x);

        _requestsView.ObserveCountChanged(notifyCurrentCount: true)
            .Subscribe(count => _pendingCount.Value = count)
            .AddTo(ref _bag);

        _reloadRequests
            .ThrottleFirstLast(CoalesceWindow, _timeProvider)
            .SubscribeAwait(async (personaId, ct) =>
            {
                await LoadRequestsInternalAsync(personaId, requireAlreadyActive: true, ct).ConfigureAwait(false);
            }, AwaitOperation.Switch)
            .AddTo(ref _bag);

        eventSubscriber.ObserveEvent<InboundContactRequestReceivedEvent>()
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

        eventSubscriber.ObserveEvent<ContactRequestApprovedEvent>()
            .Subscribe(evt => HandleRequestRemovedEvent(evt.OwnerIdentityId, evt.RemotePeerId))
            .AddTo(ref _bag);

        eventSubscriber.ObserveEvent<ContactRequestRejectedEvent>()
            .Subscribe(evt => HandleRequestRemovedEvent(evt.OwnerIdentityId, evt.RemotePeerId))
            .AddTo(ref _bag);

        if (_currentPersonaService.CurrentPersona is { } initialPersona && initialPersona.IdentityId.IsValid)
        {
            _ = LoadRequestsInternalAsync(initialPersona.IdentityId, requireAlreadyActive: false, _disposeCts.Token);
        }

        _currentPersonaService.CurrentPersonaChanged
            .SubscribeAwait(async (persona, ct) =>
            {
                if (persona != null && persona.IdentityId.IsValid)
                {
                    await LoadRequestsInternalAsync(persona.IdentityId, requireAlreadyActive: false, ct).ConfigureAwait(false);
                }
                else
                {
                    CancelAndClearActivePersona();
                }
            }, AwaitOperation.Switch)
            .AddTo(ref _bag);
    }

    public ReadOnlyReactiveProperty<int> PendingCount => _pendingCount;

    public ISynchronizedView<InboundPendingRequestModel, InboundPendingRequestModel> Requests => _requestsView;

    public Task LoadRequestsForPersonaAsync(PublicIdentityId personaId, CancellationToken ct = default)
        => LoadRequestsInternalAsync(personaId, requireAlreadyActive: false, ct);

    private async Task LoadRequestsInternalAsync(
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
                _requestsList.Clear();
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

            _logger.LogInformation("Loading quarantined inbound requests for persona {PersonaId}", personaId);

            try
            {
                var summaries = await _contactQueryService.GetContactSummariesAsync(personaId, linkedCts.Token).ConfigureAwait(false);
                var pending = summaries.Where(c => c.State == ContactState.PendingApproval).ToList();

                lock (_lock)
                {
                    if (_disposed || linkedCts.IsCancellationRequested || _loadGeneration != generation || _activePersonaId != personaId)
                    {
                        return;
                    }

                    _requestsList.Clear();
                    foreach (var item in pending)
                    {
                        var hex = item.IdentityId.Value.ToString("N");
                        var preview = hex.Length >= 8 ? $"{hex[..4]}...{hex[^4..]}" : hex;
                        _requestsList.Add(new InboundPendingRequestModel(
                            item.IdentityId,
                            string.IsNullOrWhiteSpace(item.DisplayName) ? $"Peer {preview}" : item.DisplayName,
                            preview,
                            item.AddedAtUtc));
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Expected when superseded by AwaitOperation.Switch, persona switch, or disposal
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load inbound requests for persona {PersonaId}", personaId);
            }
        }
    }

    private void HandleRequestRemovedEvent(PublicIdentityId ownerIdentityId, PublicIdentityId remotePeerId)
    {
        PublicIdentityId? reloadPersonaId = null;

        lock (_lock)
        {
            if (_disposed || !_activePersonaId.HasValue || ownerIdentityId != _activePersonaId.Value)
            {
                return;
            }

            var existing = _requestsList.FirstOrDefault(r => r.RequesterId == remotePeerId);
            if (existing != null)
            {
                _requestsList.Remove(existing);
            }
            else
            {
                reloadPersonaId = _activePersonaId.Value;
            }
        }

        if (reloadPersonaId.HasValue)
        {
            TriggerReload(reloadPersonaId.Value);
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
            _requestsList.Clear();
            oldCts = _personaCts;
            _personaCts = new CancellationTokenSource();
        }

        oldCts.Cancel();
        oldCts.Dispose();
    }

    public async Task AcceptRequestAsync(PublicIdentityId personaId, PublicIdentityId requesterId, CancellationToken ct = default)
    {
        if (_disposed || !personaId.IsValid || !requesterId.IsValid) return;

        _logger.LogInformation("Accepting inbound request from {RequesterId} for persona {PersonaId}", requesterId, personaId);

        var result = await _inboundWorkflow.AcceptRequestAsync(personaId, requesterId, ct).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            _logger.LogWarning("Failed to accept inbound request from {RequesterId}: {Error}", requesterId, result.Error.Description);
            return;
        }

        lock (_lock)
        {
            var existing = _requestsList.FirstOrDefault(r => r.RequesterId == requesterId);
            if (existing != null)
            {
                _requestsList.Remove(existing);
            }
        }
    }

    public async Task RejectRequestAsync(PublicIdentityId personaId, PublicIdentityId requesterId, CancellationToken ct = default)
    {
        if (_disposed || !personaId.IsValid || !requesterId.IsValid) return;

        _logger.LogInformation("Rejecting inbound request from {RequesterId} for persona {PersonaId}", requesterId, personaId);

        var result = await _inboundWorkflow.RejectRequestAsync(personaId, requesterId, ct).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            _logger.LogWarning("Failed to reject inbound request from {RequesterId}: {Error}", requesterId, result.Error.Description);
            return;
        }

        lock (_lock)
        {
            var existing = _requestsList.FirstOrDefault(r => r.RequesterId == requesterId);
            if (existing != null)
            {
                _requestsList.Remove(existing);
            }
        }
    }

    public async Task BlockRequestAsync(PublicIdentityId personaId, PublicIdentityId requesterId, CancellationToken ct = default)
    {
        if (_disposed || !personaId.IsValid || !requesterId.IsValid) return;

        _logger.LogInformation("Blocking requester {RequesterId} per SYS-BLOCK-WIRE-INDISTINGUISHABILITY", requesterId);

        var result = await _inboundWorkflow.BlockRequestAsync(personaId, requesterId, ct).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            _logger.LogWarning("Failed to block inbound requester {RequesterId}: {Error}", requesterId, result.Error.Description);
            return;
        }

        lock (_lock)
        {
            var existing = _requestsList.FirstOrDefault(r => r.RequesterId == requesterId);
            if (existing != null)
            {
                _requestsList.Remove(existing);
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

        _requestsView.Dispose();
        _pendingCount.Dispose();

        personaCtsToDispose.Dispose();
        _disposeCts.Dispose();
    }
}
