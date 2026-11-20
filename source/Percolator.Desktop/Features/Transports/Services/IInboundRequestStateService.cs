// SPECIFICATION GOVERNANCE: Read Percolator.Desktop/spec/UiStateService.spec.md before modifying this file.

namespace Percolator.Desktop.Features.Transports.Services;

using System;
using System.Threading;
using System.Threading.Tasks;
using ObservableCollections;
using Percolator.Desktop.Features.Transports.Models;
using Percolator.Domain.Identities;
using R3;

/// <summary>
/// Session-scoped UI state service owning quarantined inbound unsolicited contact requests
/// and driving the Left Rail pending requests notification badge ([UISS-001], [DEC-PULL-BASED-CONSENT-STAGING]).
/// </summary>
public interface IInboundRequestStateService : IDisposable
{
    /// <summary>
    /// Live count of staged inbound requests awaiting user review (drives the Left Rail indicator light).
    /// </summary>
    ReadOnlyReactiveProperty<int> PendingCount { get; }

    /// <summary>
    /// Thread-safe synchronized projection of staged inbound requests for UI data binding ([UISS-005]).
    /// </summary>
    ISynchronizedView<InboundPendingRequestModel, InboundPendingRequestModel> Requests { get; }

    /// <summary>
    /// Loads or refreshes the quarantined inbound requests for the active persona using CQRS read projections ([VM-006]).
    /// </summary>
    Task LoadRequestsForPersonaAsync(PublicIdentityId personaId, CancellationToken ct = default);

    /// <summary>
    /// Accepts an inbound request: approves contact and completes cryptographic handshake.
    /// </summary>
    Task AcceptRequestAsync(PublicIdentityId personaId, PublicIdentityId requesterId, CancellationToken ct = default);

    /// <summary>
    /// Rejects an inbound request silently without wire feedback to the initiator.
    /// </summary>
    Task RejectRequestAsync(PublicIdentityId personaId, PublicIdentityId requesterId, CancellationToken ct = default);

    /// <summary>
    /// Blocks an inbound requester: destroys handshake and permanently blacklists peer ID per SYS-BLOCK-WIRE-INDISTINGUISHABILITY.
    /// </summary>
    Task BlockRequestAsync(PublicIdentityId personaId, PublicIdentityId requesterId, CancellationToken ct = default);
}
