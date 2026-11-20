namespace Percolator.Infrastructure.Persistence.Repositories;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Percolator.Domain.Identities.Model;
using Percolator.Domain.Identities.Ports;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Infrastructure.Persistence.Entities;

/// <summary>Singleton over the pooled context factory — creates/disposes a context per call.</summary>
public sealed class SqliteIdentityProfileRepository : IIdentityProfileRepository
{
    private readonly IDbContextFactory<PercolatorDbContext> _dbFactory;

    public SqliteIdentityProfileRepository(IDbContextFactory<PercolatorDbContext> dbFactory)
    {
        _dbFactory = dbFactory ?? throw new ArgumentNullException(nameof(dbFactory));
    }

    public async Task<IdentityProfile?> GetByIdAsync(Domain.Identities.PublicIdentityId id, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var idGuid = id.Value;
        var entity = await db.IdentityProfiles
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == idGuid, cancellationToken);

        if (entity == null)
        {
            return null;
        }

        return IdentityProfile.Rehydrate(
            new Domain.Identities.PublicIdentityId(entity.Id),
            entity.DisplayName,
            (IdentityRole)entity.Role,
            (IdentityState)entity.State,
            entity.CreatedAtUtc,
            entity.DisabledAtUtc);
    }

    public async Task<IReadOnlyList<IdentityProfile>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var entities = await db.IdentityProfiles
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        return entities.Select(entity => IdentityProfile.Rehydrate(
            new Domain.Identities.PublicIdentityId(entity.Id),
            entity.DisplayName,
            (IdentityRole)entity.Role,
            (IdentityState)entity.State,
            entity.CreatedAtUtc,
            entity.DisabledAtUtc)).ToList();
    }

    public async Task SaveAsync(IdentityProfile profile, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var idGuid = profile.Id.Value;
        var existing = await db.IdentityProfiles
            .FirstOrDefaultAsync(p => p.Id == idGuid, cancellationToken);

        if (existing == null)
        {
            var entity = new IdentityProfileEntity
            {
                Id = idGuid,
                DisplayName = profile.DisplayName,
                Role = (int)profile.Role,
                State = (int)profile.State,
                CreatedAtUtc = profile.CreatedAtUtc,
                DisabledAtUtc = profile.DisabledAtUtc
            };
            await db.IdentityProfiles.AddAsync(entity, cancellationToken);
        }
        else
        {
            existing.DisplayName = profile.DisplayName;
            existing.Role = (int)profile.Role;
            existing.State = (int)profile.State;
            existing.DisabledAtUtc = profile.DisabledAtUtc;
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteAsync(Domain.Identities.PublicIdentityId id, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var idGuid = id.Value;
        await db.IdentityProfiles
            .Where(p => p.Id == idGuid)
            .ExecuteDeleteAsync(cancellationToken);
    }
}
