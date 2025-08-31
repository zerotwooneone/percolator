using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Percolator.Application.Identity;
using Percolator.Infrastructure.Persistence;

namespace Percolator.Infrastructure.Identity
{
    public sealed class SqliteSelfIdentityRepository : ISelfIdentityRepository
    {
        private readonly PercolatorDbContext _db;

        public SqliteSelfIdentityRepository(PercolatorDbContext db)
        {
            _db = db;
        }

        public async Task<SelfIdentityDto?> GetByIdAsync(int id)
        {
            var dbo = await _db.SelfIdentities.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
            return dbo is null ? null : Map(dbo);
        }

        public async Task<SelfIdentityDto?> GetByPeerIdAsync(Guid peerId)
        {
            var dbo = await _db.SelfIdentities.AsNoTracking().FirstOrDefaultAsync(x => x.PeerId == peerId);
            return dbo is null ? null : Map(dbo);
        }

        public async Task<SelfIdentityDto?> GetByNameAsync(string name)
        {
            var dbo = await _db.SelfIdentities.AsNoTracking().FirstOrDefaultAsync(x => x.Name == name);
            return dbo is null ? null : Map(dbo);
        }

        public async Task<IReadOnlyList<SelfIdentityDto>> ListAsync()
        {
            var items = await _db.SelfIdentities.AsNoTracking().OrderBy(x => x.Id).ToListAsync();
            return items.Select(Map).ToList();
        }

        public async Task<int> CreateAsync(Guid peerId, string name)
        {
            var dbo = new SelfIdentityDbo
            {
                PeerId = peerId,
                Name = name
            };
            _db.SelfIdentities.Add(dbo);
            await _db.SaveChangesAsync();
            return dbo.Id;
        }

        public async Task<bool> AddKnownPeerAsync(int selfIdentityId, Guid peerId)
        {
            var exists = await _db.SelfIdentityKnownPeers.AsNoTracking()
                .AnyAsync(x => x.SelfIdentityId == selfIdentityId && x.PeerId == peerId);
            if (exists)
            {
                return false;
            }
            _db.SelfIdentityKnownPeers.Add(new SelfIdentityKnownPeerDbo
            {
                SelfIdentityId = selfIdentityId,
                PeerId = peerId
            });
            await _db.SaveChangesAsync();
            return true;
        }

        public async Task<IReadOnlyList<Guid>> ListKnownPeersAsync(int selfIdentityId)
        {
            return await _db.SelfIdentityKnownPeers.AsNoTracking()
                .Where(x => x.SelfIdentityId == selfIdentityId)
                .Select(x => x.PeerId)
                .ToListAsync();
        }

        private static SelfIdentityDto Map(SelfIdentityDbo dbo)
        {
            return new SelfIdentityDto
            {
                Id = dbo.Id,
                PeerId = dbo.PeerId,
                Name = dbo.Name
            };
        }
    }
}
