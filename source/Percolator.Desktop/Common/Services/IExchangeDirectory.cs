// SPECIFICATION GOVERNANCE: Read Percolator.Desktop/spec/ViewModel.spec.md before modifying this file.

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Percolator.Application2.Ports;
using Percolator.Desktop.Features.Startup.Models;
using Percolator.Domain.Common;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Desktop.Common.Services;

/// <summary>
/// Presentation-facing exchange (relay) facade ([VM-004], [VM-006]): ViewModels stay free of
/// session internals. Every operation runs inside a scope on the authenticated session so all
/// link/unlink state lands in the encrypted vault — the gate performs zero wire traffic
/// ([DEP-DESKTOP-PRE-ACTIVATION-STEALTH]).
/// </summary>
public interface IExchangeDirectory
{
    /// <summary>Lists exchanges merged with their linked personas and saved port preferences.</summary>
    Task<IReadOnlyList<ExchangeDirectoryEntry>> ListAsync(
        IAuthenticatedSession session, CancellationToken ct = default);

    /// <summary>
    /// Creates a self-hosted exchange: relay-host identity profile + registration in the vault,
    /// with the optionally pre-linked personas. The tile appears offline — nothing listens yet.
    /// </summary>
    Task<ExchangeDirectoryEntry> CreateSelfHostedAsync(
        IAuthenticatedSession session,
        PublicIdentityId relayIdentityId,
        string displayName,
        int preferredPort,
        IReadOnlyList<PublicIdentityId> initialLinkedPersonaIds,
        CancellationToken ct = default);

    /// <summary>Links a persona to an exchange — a pure vault-local row write.</summary>
    Task<DomainResult> LinkPersonaAsync(
        IAuthenticatedSession session,
        PublicIdentityId relayIdentityId,
        PublicIdentityId personaId,
        CancellationToken ct = default);

    /// <summary>
    /// "Stop using this exchange" — a pure vault-local row delete. When
    /// <paramref name="purgeQueuedEnvelopes"/> is set and the exchange is self-hosted, the
    /// relay-side queue lives in this same vault and is drained locally first.
    /// </summary>
    Task<DomainResult> UnlinkPersonaAsync(
        IAuthenticatedSession session,
        PublicIdentityId relayIdentityId,
        PublicIdentityId personaId,
        bool purgeQueuedEnvelopes,
        CancellationToken ct = default);

    /// <summary>
    /// Starts the session's relay-facing transport endpoints (mapRelayEndpoints) for a selected
    /// self-hosted exchange. Returns the bound port, or <c>null</c> when the transport was
    /// already running.
    /// </summary>
    Task<int?> StartSelfHostedEndpointAsync(
        IAuthenticatedSession session,
        ExchangeLaunchDescriptor exchange,
        CancellationToken ct = default);

    /// <summary>
    /// Provisions mailboxes (registration + pre-key publication) for each persona against every
    /// exchange it is linked to. Aggregates failures rather than aborting the launch.
    /// </summary>
    Task<DomainResult> ProvisionLinkedMailboxesAsync(
        IAuthenticatedSession session,
        IEnumerable<PublicIdentityId> personaIds,
        CancellationToken ct = default);
}
