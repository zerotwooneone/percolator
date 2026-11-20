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
using Percolator.Desktop.Common.Services;
using Percolator.Desktop.Features.Transports.Models;
using Percolator.Domain.Identities;
using Percolator.Domain.Identities.Model;
using R3;

/// <summary>
/// Free-threaded UI state service managing the quarantined inbound contact requests queue
/// and driving the Left Rail indicator light ([UISS-001], [UISS-002], [DEC-PULL-BASED-CONSENT-STAGING]).
/// </summary>
public sealed class InboundRequestStateService : IInboundRequestStateService
{
    private readonly IPeerContactQueryService _contactQueryService;
    private readonly IInboundConnectionWorkflow _inboundWorkflow;
    private readonly ICurrentPersonaService _currentPersonaService;
    private readonly ILogger<InboundRequestStateService> _logger;

    private readonly ObservableList<InboundPendingRequestModel> _requestsList = new();
    private readonly ISynchronizedView<InboundPendingRequestModel, InboundPendingRequestModel> _requestsView;
    private readonly ReactiveProperty<int> _pendingCount = new(0);
    private readonly object _lock = new();

    private DisposableBag _bag;
    private PublicIdentityId? _activePersonaId;
    private bool _disposed;

    public InboundRequestStateService(
        IPeerContactQueryService contactQueryService,
        IInboundConnectionWorkflow inboundWorkflow,
        ICurrentPersonaService currentPersonaService,
        ILogger<InboundRequestStateService> logger)
    {
        _contactQueryService = contactQueryService ?? throw new ArgumentNullException(nameof(contactQueryService));
        _inboundWorkflow = inboundWorkflow ?? throw new ArgumentNullException(nameof(inboundWorkflow));
        _currentPersonaService = currentPersonaService ?? throw new ArgumentNullException(nameof(currentPersonaService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _requestsView = _requestsList.CreateView(x => x);

        _requestsView.ObserveCountChanged(notifyCurrentCount: true)
            .Subscribe(count => _pendingCount.Value = count)
            .AddTo(ref _bag);

        if (_currentPersonaService.CurrentPersona is { } initialPersona && initialPersona.IdentityId.IsValid)
        {
            _activePersonaId = initialPersona.IdentityId;
            _ = LoadRequestsForPersonaAsync(initialPersona.IdentityId);
        }

        _currentPersonaService.CurrentPersonaChanged
            .Subscribe(persona =>
            {
                if (persona != null && persona.IdentityId.IsValid)
                {
                    _ = LoadRequestsForPersonaAsync(persona.IdentityId);
                }
                else
                {
                    lock (_lock)
                    {
                        _activePersonaId = null;
                        _requestsList.Clear();
                    }
                }
            })
            .AddTo(ref _bag);
    }

    public ReadOnlyReactiveProperty<int> PendingCount => _pendingCount;

    public ISynchronizedView<InboundPendingRequestModel, InboundPendingRequestModel> Requests => _requestsView;

    public async Task LoadRequestsForPersonaAsync(PublicIdentityId personaId, CancellationToken ct = default)
    {
        if (_disposed || !personaId.IsValid) return;

        _logger.LogInformation("Loading quarantined inbound requests for persona {PersonaId}", personaId);

        try
        {
            var summaries = await _contactQueryService.GetContactSummariesAsync(personaId, ct).ConfigureAwait(false);
            var pending = summaries.Where(c => c.State == ContactState.PendingApproval).ToList();

            lock (_lock)
            {
                _activePersonaId = personaId;
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
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load inbound requests for persona {PersonaId}", personaId);
        }
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
        if (_disposed) return;
        _disposed = true;

        _bag.Dispose();
        _requestsView.Dispose();
        _pendingCount.Dispose();
    }
}
