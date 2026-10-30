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

public sealed class SqliteIdentityProfileRepository : IIdentityProfileRepository
{
    private readonly PercolatorDbContext _dbContext;

    public SqliteIdentityProfileRepository(PercolatorDbContext dbContext)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    public async Task<IdentityProfile?> GetByIdAsync(PublicIdentityId id, CancellationToken cancellationToken = default)
    {
        var idGuid = id.Value;
        var entity = await _dbContext.IdentityProfiles
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == idGuid, cancellationToken);

        if (entity == null)
        {
            return null;
        }

        return IdentityProfile.Rehydrate(
            new PublicIdentityId(entity.Id),
            entity.DisplayName,
            (IdentityRole)entity.Role,
            (IdentityState)entity.State,
            entity.CreatedAtUtc,
            entity.DisabledAtUtc);
    }

    public async Task<IReadOnlyList<IdentityProfile>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var entities = await _dbContext.IdentityProfiles
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        return entities.Select(entity => IdentityProfile.Rehydrate(
            new PublicIdentityId(entity.Id),
            entity.DisplayName,
            (IdentityRole)entity.Role,
            (IdentityState)entity.State,
            entity.CreatedAtUtc,
            entity.DisabledAtUtc)).ToList();
    }

    public async Task SaveAsync(IdentityProfile profile, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var idGuid = profile.Id.Value;
        var existing = await _dbContext.IdentityProfiles
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
            await _dbContext.IdentityProfiles.AddAsync(entity, cancellationToken);
        }
        else
        {
            existing.DisplayName = profile.DisplayName;
            existing.Role = (int)profile.Role;
            existing.State = (int)profile.State;
            existing.DisabledAtUtc = profile.DisabledAtUtc;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
    }
}
