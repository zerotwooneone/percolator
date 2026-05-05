using Microsoft.EntityFrameworkCore;
using Percolator.Identity;
using Percolator.Identity.Model;
using Percolator.Infrastructure.Persistence;

namespace Percolator.Infrastructure.Identity;

public sealed class SqliteSelfIdentityDomainRepository : ISelfIdentityRepository
{
    private readonly PercolatorDbContext _db;

    public SqliteSelfIdentityDomainRepository(PercolatorDbContext db)
    {
        _db = db;
    }

    public async Task<SelfIdentity?> GetMostRecentAsync(CancellationToken ct = default)
    {
        var dbo = await _db.SelfIdentities
            .AsNoTracking()
            .OrderByDescending(x => x.LastUsedUtc)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
        return dbo is null ? null : Map(dbo);
    }

    public async Task<SelfIdentity?> GetByIdAsync(SelfId id, CancellationToken ct = default)
    {
        var dbo = await _db.SelfIdentities
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id.Value, ct)
            .ConfigureAwait(false);
        return dbo is null ? null : Map(dbo);
    }

    public async Task<IReadOnlyList<SelfIdentity>> ListAsync(CancellationToken ct = default)
    {
        var items = await _db.SelfIdentities
            .AsNoTracking()
            .OrderBy(x => x.Id)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        return items.Select(Map).ToList();
    }

    public async Task<SelfId> CreateAsync(SelfIdentity identity, CancellationToken ct = default)
    {
        if (identity is null) throw new ArgumentNullException(nameof(identity));
        if (identity.Id.Value != 0)
        {
            throw new InvalidOperationException("CreateAsync requires identity.Id to be 0 (unsaved).");
        }

        var dbo = new SelfIdentityDbo
        {
            PeerId = identity.PeerId.Value,
            Name = identity.DisplayName?.Value ?? string.Empty,
            LastUsedUtc = identity.LastUsedUtc
        };
        _db.SelfIdentities.Add(dbo);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        return new SelfId(dbo.Id);
    }

    public async Task SaveAsync(SelfIdentity identity, CancellationToken ct = default)
    {
        if (identity.Id.Value == 0)
        {
            // Delegate to Create for inserts
            await CreateAsync(identity, ct).ConfigureAwait(false);
        }
        else
        {
            // Update
            var dbo = await _db.SelfIdentities.FirstOrDefaultAsync(x => x.Id == identity.Id.Value, ct).ConfigureAwait(false);
            if (dbo is null)
            {
                // Upsert semantics: create if missing
                dbo = new SelfIdentityDbo
                {
                    Id = identity.Id.Value,
                    PeerId = identity.PeerId.Value,
                    Name = identity.DisplayName?.Value ?? string.Empty,
                    LastUsedUtc = identity.LastUsedUtc
                };
                _db.SelfIdentities.Add(dbo);
            }
            else
            {
                dbo.Name = identity.DisplayName?.Value ?? dbo.Name;
                dbo.LastUsedUtc = identity.LastUsedUtc;
            }
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
    }

    private static SelfIdentity Map(SelfIdentityDbo dbo)
    {
        var self = new SelfIdentity(new SelfId(dbo.Id), new PeerId(dbo.PeerId));
        if (!string.IsNullOrWhiteSpace(dbo.Name)) self.SetDisplayName(dbo.Name);
        self.TouchLastUsed(dbo.LastUsedUtc);
        return self;
    }
}
