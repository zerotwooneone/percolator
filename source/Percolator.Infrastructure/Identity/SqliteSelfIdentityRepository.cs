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
        
        private async Task<SelfIdentityDto?> GetByNameAsync(string name)
        {
            var dbo = await _db.SelfIdentities.AsNoTracking().FirstOrDefaultAsync(x => x.Name == name);
            return dbo is null ? null : Map(dbo);
        }
        
        public async Task<SelfIdentityDto?> GetByNameWithFallbackAsync(string name, string fallbackName)
        {
            if (string.IsNullOrWhiteSpace(fallbackName))
            {
                return await GetByNameAsync(name);
            }
            var identityDbos = await _db.SelfIdentities.AsNoTracking()
                .Where(x => x.Name == name || x.Name == fallbackName).ToListAsync();
            if (identityDbos.Count == 0)
            {
                return null;
            }
            var target = identityDbos.FirstOrDefault(x => x.Name == name) ?? 
                         identityDbos.FirstOrDefault(x => x.Name == fallbackName);
            if (target is null)
            {
                return null;
            }
            return Map(target);
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
