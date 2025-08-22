using Microsoft.EntityFrameworkCore;
using Percolator.Dht;
using Percolator.Infrastructure.Persistence;

namespace Percolator.Infrastructure.Dht;

public class SqliteDhtNodeRepository : IDhtNodeRepository
{
    private readonly PercolatorDbContext _context;

    public SqliteDhtNodeRepository(PercolatorDbContext context)
    {
        _context = context;
    }

    public async Task AddAsync(DhtNode node)
    {
        await _context.DhtNodes.AddAsync(node);
        await _context.SaveChangesAsync();
    }

    public async Task<DhtNode?> GetAsync(NodeId id)
    {
        return await _context.DhtNodes.FindAsync(id);
    }

    public async Task UpdateAsync(DhtNode node)
    {
        _context.DhtNodes.Update(node);
        await _context.SaveChangesAsync();
    }

    public async Task<IEnumerable<DhtNode>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        return await _context.DhtNodes.ToListAsync(cancellationToken);
    }
}
