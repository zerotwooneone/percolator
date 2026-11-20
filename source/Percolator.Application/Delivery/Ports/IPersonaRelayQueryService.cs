namespace Percolator.Application.Delivery.Ports;

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities;

/// <summary>
/// Lightweight read-projection model for a relay linked to a persona.
/// Used by presentation surfaces without loading write-heavy domain aggregates.
/// </summary>
public sealed record PersonaRelayLinkReadModel(
    PublicIdentityId RelayIdentityId,
    string DisplayName,
    RelayEndpoint Endpoint,
    bool IsLocalRelayHost,
    RelayLinkStatus Status);

/// <summary>
/// CQRS query service for projecting persona-relay associations and operational link status.
/// </summary>
public interface IPersonaRelayQueryService
{
    Task<IReadOnlyList<PersonaRelayLinkReadModel>> GetRelayLinksForPersonaAsync(
        PublicIdentityId personaId,
        CancellationToken ct = default);
}
