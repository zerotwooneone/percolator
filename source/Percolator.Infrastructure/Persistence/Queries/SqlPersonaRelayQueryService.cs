namespace Percolator.Infrastructure.Persistence.Queries;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Percolator.Application.Delivery.Ports;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities;
using Percolator.Infrastructure.Persistence;

/// <summary>
/// CQRS query service reading lightweight projection models of persona relay links directly from SQLite.
/// Avoids loading write-heavy RelayRegistration aggregates into memory.
/// </summary>
public sealed class SqlPersonaRelayQueryService : IPersonaRelayQueryService
{
    private readonly PercolatorDbContext _db;

    public SqlPersonaRelayQueryService(PercolatorDbContext db)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    public async Task<IReadOnlyList<PersonaRelayLinkReadModel>> GetRelayLinksForPersonaAsync(
        PublicIdentityId personaId,
        CancellationToken ct = default)
    {
        var ownerGuid = personaId.Value;
        var query = from b in _db.RelayMailboxBindings.AsNoTracking()
                    join r in _db.RelayRegistrations.AsNoTracking() on b.RelayIdentityId equals r.RelayIdentityId
                    join p in _db.IdentityProfiles.AsNoTracking() on r.RelayIdentityId equals p.Id into profiles
                    from prof in profiles.DefaultIfEmpty()
                    where b.OwnerIdentityId == ownerGuid
                    select new
                    {
                        RelayId = r.RelayIdentityId,
                        Endpoint = r.Endpoint,
                        DisplayName = prof != null ? prof.DisplayName : r.Endpoint,
                        IsLocalHost = prof != null && prof.Role == 2, // IdentityRole.RelayHost
                        Status = (RelayLinkStatus)b.Status
                    };


        var items = await query.ToListAsync(ct).ConfigureAwait(false);
        return items.Select(x => new PersonaRelayLinkReadModel(
            new PublicIdentityId(x.RelayId),
            x.DisplayName,
            RelayEndpoint.Create(x.Endpoint).Value!,
            x.IsLocalHost,
            x.Status)).ToList();
    }
}
