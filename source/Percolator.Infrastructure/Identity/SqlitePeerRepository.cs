using Microsoft.EntityFrameworkCore;
using Percolator.Identity;
using Percolator.Infrastructure.Persistence;

namespace Percolator.Infrastructure.Identity;

public class SqlitePeerRepository : IPeerRepository
{
    private readonly PercolatorDbContext _context;

    public SqlitePeerRepository(PercolatorDbContext context)
    {
        _context = context;
    }

    public async Task<Peer?> GetByIdAsync(PeerId peerId)
    {
        return await _context.Peers.FindAsync(peerId);
    }

    public async Task AddAsync(Peer peer)
    {
        _context.Peers.Add(peer);
        await _context.SaveChangesAsync();
    }

    public async Task AddOrUpdateAsync(Peer peer)
    {
        var existingById = await _context.Peers.FirstOrDefaultAsync(p => p.Id == peer.Id);
        if (existingById is not null)
        {
            existingById.Name = peer.Name;
            await _context.SaveChangesAsync();
            return;
        }

        var existingByName = await _context.Peers.FirstOrDefaultAsync(p => p.Name == peer.Name);
        if (existingByName is not null)
        {
            // Keep existing record; no change in Id. Optionally could reconcile IDs if business rules allow.
            return;
        }

        _context.Peers.Add(peer);
        await _context.SaveChangesAsync();
    }

    public async Task RemoveAsync(PeerId peerId)
    {
        var peer = await _context.Peers.FindAsync(peerId);
        if (peer is not null)
        {
            _context.Peers.Remove(peer);
            await _context.SaveChangesAsync();
        }
    }

    public async Task<Peer?> GetByNameAsync(string name)
    {
        return await _context.Peers.FirstOrDefaultAsync(p => p.Name == name);
    }
}
