// SPECIFICATION GOVERNANCE: Read Percolator.Desktop/spec/UiStateService.spec.md before modifying this file.

namespace Percolator.Desktop.Features.Transports.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using ObservableCollections;
using Percolator.Application.Delivery.Ports;
using Percolator.Desktop.Common.Services;
using Percolator.Desktop.Features.Transports.Models;
using Percolator.Domain.Delivery.Model;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities;
using R3;

/// <summary>
/// Free-threaded UI presentation service tracking configured relays (post offices) for the active persona.
/// Manages live status telemetry, Left Rail ordering, and encapsulates mutations behind semantic methods ([UISS-001], [UISS-005]).
/// </summary>
public sealed class RelayStateService : IRelayStateService
{
    private readonly IPersonaRelayQueryService _personaRelayQueryService;
    private readonly IRelayDirectory _relayDirectory;
    private readonly ICurrentPersonaService _currentPersonaService;
    private readonly ILogger<RelayStateService> _logger;

    private readonly ObservableList<RelayChannelModel> _relaysList = new();
    private readonly ISynchronizedView<RelayChannelModel, RelayChannelModel> _relaysView;
    private readonly ReactiveProperty<int> _totalRelayUnreadCount = new(0);
    private readonly object _lock = new();

    private DisposableBag _bag;
    private PublicIdentityId? _activePersonaId;
    private bool _disposed;

    public RelayStateService(
        IPersonaRelayQueryService personaRelayQueryService,
        IRelayDirectory relayDirectory,
        ICurrentPersonaService currentPersonaService,
        ILogger<RelayStateService> logger)
    {
        _personaRelayQueryService = personaRelayQueryService ?? throw new ArgumentNullException(nameof(personaRelayQueryService));
        _relayDirectory = relayDirectory ?? throw new ArgumentNullException(nameof(relayDirectory));
        _currentPersonaService = currentPersonaService ?? throw new ArgumentNullException(nameof(currentPersonaService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _relaysView = _relaysList.CreateView(x => x);

        if (_currentPersonaService.CurrentPersona is { } initialPersona && initialPersona.IdentityId.IsValid)
        {
            _activePersonaId = initialPersona.IdentityId;
            _ = LoadRelaysForPersonaAsync(initialPersona.IdentityId);
        }

        _currentPersonaService.CurrentPersonaChanged
            .Subscribe(persona =>
            {
                if (persona != null && persona.IdentityId.IsValid)
                {
                    _ = LoadRelaysForPersonaAsync(persona.IdentityId);
                }
                else
                {
                    lock (_lock)
                    {
                        _activePersonaId = null;
                        _relaysList.Clear();
                    }
                }
            })
            .AddTo(ref _bag);
    }

    public ISynchronizedView<RelayChannelModel, RelayChannelModel> Relays => _relaysView;

    public ReadOnlyReactiveProperty<int> TotalRelayUnreadCount => _totalRelayUnreadCount;

    public async Task LoadRelaysForPersonaAsync(PublicIdentityId personaId, CancellationToken ct = default)
    {
        if (_disposed || !personaId.IsValid) return;

        _logger.LogInformation("Loading configured relays for persona {PersonaId}", personaId);

        IReadOnlyList<PersonaRelayLinkReadModel> links;
        try
        {
            links = await _personaRelayQueryService.GetRelayLinksForPersonaAsync(personaId, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load relay links for persona {PersonaId}", personaId);
            return;
        }

        lock (_lock)
        {
            _activePersonaId = personaId;
            _relaysList.Clear();

            int order = 0;
            foreach (var link in links)
            {
                var status = link.Status switch
                {
                    RelayLinkStatus.Enabled => RelayConnectionStatus.Connected,
                    RelayLinkStatus.Disabled => RelayConnectionStatus.Disconnected,
                    _ => RelayConnectionStatus.Disconnected
                };

                var model = new RelayChannelModel(
                    relayIdentityId: link.RelayIdentityId,
                    displayName: link.DisplayName,
                    endpoint: link.Endpoint,
                    status: status,
                    latencyMs: 0,
                    unreadCount: 0,
                    folderId: null,
                    displayOrder: order++,
                    isLocalRelayHost: link.IsLocalRelayHost);

                _relaysList.Add(model);
            }
        }

        UpdateTotalUnread();
    }

    public Task<RelayChannelModel> AddRelayAsync(
        PublicIdentityId personaId,
        string endpoint,
        string? friendlyName = null,
        CancellationToken ct = default)
    {
        return AddRelayAsync(personaId, RelayEndpoint.Parse(endpoint), friendlyName, ct);
    }

    public async Task<RelayChannelModel> AddRelayAsync(
        PublicIdentityId personaId,
        RelayEndpoint endpoint,
        string? friendlyName = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!personaId.IsValid) throw new ArgumentException("Invalid persona ID", nameof(personaId));

        _logger.LogInformation("Adding relay {Endpoint} for persona {PersonaId}", endpoint, personaId);

        RelayChannelModel? existingModel = null;
        lock (_lock)
        {
            existingModel = _relaysList.FirstOrDefault(r => r.Endpoint.Equals(endpoint));
        }

        if (existingModel != null)
        {
            _logger.LogInformation("Relay {Endpoint} is already present in state for persona {PersonaId}", endpoint, personaId);
            return existingModel;
        }

        // Ensure relay is registered in directory
        var existingEntries = await _relayDirectory.ListAsync(ct).ConfigureAwait(false);
        var entry = existingEntries.FirstOrDefault(e => e.Endpoint.Equals(endpoint));
        PublicIdentityId relayIdentityId;

        if (entry != null)
        {
            relayIdentityId = entry.RelayIdentityId;
        }
        else
        {
            relayIdentityId = PublicIdentityId.New();
            var regResult = await _relayDirectory.RegisterRemoteRelayAsync(relayIdentityId, endpoint, pinnedCertificate: null, ct: ct).ConfigureAwait(false);
            if (regResult.IsFailure)
            {
                _logger.LogWarning("RegisterRemoteRelayAsync failed for {Endpoint}: {Error}", endpoint, regResult.Error.Description);
            }
        }

        // Ensure identity is linked
        var linkResult = await _relayDirectory.LinkIdentityAsync(relayIdentityId, personaId, ct: ct).ConfigureAwait(false);
        if (linkResult.IsFailure)
        {
            _logger.LogWarning("LinkIdentityAsync failed for relay {RelayId} and persona {PersonaId}: {Error}",
                relayIdentityId, personaId, linkResult.Error.Description);
        }

        var displayName = !string.IsNullOrWhiteSpace(friendlyName)
            ? friendlyName
            : (entry != null && !string.IsNullOrWhiteSpace(entry.DisplayName) ? entry.DisplayName : endpoint.ToString());

        RelayChannelModel model;
        lock (_lock)
        {
            model = new RelayChannelModel(
                relayIdentityId: relayIdentityId,
                displayName: displayName,
                endpoint: endpoint,
                status: RelayConnectionStatus.Connected,
                latencyMs: 0,
                unreadCount: 0,
                folderId: null,
                displayOrder: _relaysList.Count,
                isLocalRelayHost: entry?.IsLocalRelayHost ?? false);

            _relaysList.Add(model);
        }

        UpdateTotalUnread();
        return model;
    }

    public async Task RemoveRelayAsync(PublicIdentityId personaId, PublicIdentityId relayId, CancellationToken ct = default)
    {
        if (!personaId.IsValid || !relayId.IsValid) return;

        _logger.LogInformation("Removing relay {RelayId} for persona {PersonaId}", relayId, personaId);

        try
        {
            await _relayDirectory.UnlinkIdentityAsync(relayId, personaId, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to unlink relay {RelayId} from directory", relayId);
        }

        lock (_lock)
        {
            var match = _relaysList.FirstOrDefault(r => r.RelayIdentityId == relayId);
            if (match != null)
            {
                _relaysList.Remove(match);
            }
        }

        UpdateTotalUnread();
    }

    public Task ReorderRelaysAsync(PublicIdentityId personaId, IReadOnlyList<PublicIdentityId> orderedRelayIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(orderedRelayIds);

        lock (_lock)
        {
            for (int i = 0; i < orderedRelayIds.Count; i++)
            {
                var id = orderedRelayIds[i];
                var model = _relaysList.FirstOrDefault(r => r.RelayIdentityId == id);
                if (model != null)
                {
                    model.DisplayOrder = i;
                }
            }
        }

        return Task.CompletedTask;
    }

    public void UpdateRelayStatus(PublicIdentityId relayId, RelayConnectionStatus status, int latencyMs)
    {
        lock (_lock)
        {
            var model = _relaysList.FirstOrDefault(r => r.RelayIdentityId == relayId);
            if (model != null)
            {
                model.Status = status;
                model.LatencyMs = latencyMs;
            }
        }
    }

    private void UpdateTotalUnread()
    {
        int total = 0;
        lock (_lock)
        {
            foreach (var relay in _relaysList)
            {
                total += relay.UnreadCount;
            }
        }

        _totalRelayUnreadCount.Value = total;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _bag.Dispose();
        _relaysView.Dispose();
        _totalRelayUnreadCount.Dispose();
    }
}
