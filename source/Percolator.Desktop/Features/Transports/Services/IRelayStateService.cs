// SPECIFICATION GOVERNANCE: Read Percolator.Desktop/spec/UiStateService.spec.md before modifying this file.

namespace Percolator.Desktop.Features.Transports.Services;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ObservableCollections;
using Percolator.Desktop.Features.Transports.Models;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities;
using R3;

/// <summary>
/// Session-scoped UI state service owning the active persona's configured relays (post offices),
/// live connection health telemetry, and Left Rail vertical ordering ([UISS-001]).
/// </summary>
public interface IRelayStateService : IDisposable
{
    /// <summary>
    /// Synchronized read-only projection of configured relays for UI data binding ([UISS-005]).
    /// </summary>
    ISynchronizedView<RelayChannelModel, RelayChannelModel> Relays { get; }

    /// <summary>
    /// Total aggregate unread message count across all configured relays.
    /// </summary>
    ReadOnlyReactiveProperty<int> TotalRelayUnreadCount { get; }

    /// <summary>
    /// Loads or reloads the configured relays for the specified active persona using CQRS read projections ([VM-006]).
    /// </summary>
    Task LoadRelaysForPersonaAsync(PublicIdentityId personaId, CancellationToken ct = default);

    /// <summary>
    /// Adds and persists a new relay post office link for the persona and immediately updates the Left Rail.
    /// </summary>
    Task<RelayChannelModel> AddRelayAsync(PublicIdentityId personaId, RelayEndpoint endpoint, string? friendlyName = null, CancellationToken ct = default);

    /// <summary>
    /// Adds and persists a new relay post office link for the persona using a raw endpoint string and immediately updates the Left Rail.
    /// </summary>
    Task<RelayChannelModel> AddRelayAsync(PublicIdentityId personaId, string endpoint, string? friendlyName = null, CancellationToken ct = default);

    /// <summary>
    /// Removes a configured relay link for the persona and cleans up associated presentation state.
    /// </summary>
    Task RemoveRelayAsync(PublicIdentityId personaId, PublicIdentityId relayId, CancellationToken ct = default);

    /// <summary>
    /// Reorders the Left Rail relay list and persists the order to launch settings.
    /// </summary>
    Task ReorderRelaysAsync(PublicIdentityId personaId, IReadOnlyList<PublicIdentityId> orderedRelayIds, CancellationToken ct = default);

    /// <summary>
    /// Updates the runtime connectivity and latency telemetry for an active relay.
    /// </summary>
    void UpdateRelayStatus(PublicIdentityId relayId, RelayConnectionStatus status, int latencyMs);
}
