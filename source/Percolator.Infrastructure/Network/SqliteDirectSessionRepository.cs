using Microsoft.EntityFrameworkCore;
using Percolator.Identity;
using Percolator.Infrastructure.Persistence;
using Percolator.Network;

namespace Percolator.Infrastructure.Network;

public sealed class SqliteDirectSessionRepository : IDirectSessionRepository
{
    private readonly PercolatorDbContext _db;
    
    public SqliteDirectSessionRepository(PercolatorDbContext db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<DirectSession>> ListAsync(NetworkSelfId selfIdentityId)
    {
        var rows = await _db.DirectSessions
            .AsNoTracking()
            .Where(x => x.SelfIdentityId == selfIdentityId.Value)
            .ToListAsync()
            .ConfigureAwait(false);

        return rows
            .Select(dbo => new DirectSession(new Percolator.Network.PeerId(dbo.RemotePeerId), new DirectSessionId(dbo.SessionId)))
            .ToList();
    }

    public async Task<DirectSession?> GetBySessionIdAsync(DirectSessionId sessionId, NetworkSelfId selfIdentityId)
    {
        var dbo = await _db.DirectSessions
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.SessionId == sessionId.Value && x.SelfIdentityId == selfIdentityId.Value);
        return dbo is null ? null : new DirectSession(new Percolator.Network.PeerId(dbo.RemotePeerId), new DirectSessionId(dbo.SessionId));
    }

    public async Task<DirectSession?> GetByRemotePeerIdAsync(Percolator.Network.PeerId remotePeerId, NetworkSelfId selfIdentityId)
    {
        var dbo = await _db.DirectSessions
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.RemotePeerId == remotePeerId.Value && x.SelfIdentityId == selfIdentityId.Value);
        return dbo is null ? null : new DirectSession(new Percolator.Network.PeerId(dbo.RemotePeerId), new DirectSessionId(dbo.SessionId));
    }

    public async Task UpsertAsync(Percolator.Network.PeerId remotePeerId, DirectSessionId sessionId, NetworkSelfId selfIdentityId)
    {
        var existing = await _db.DirectSessions
            .FirstOrDefaultAsync(x => x.RemotePeerId == remotePeerId.Value && x.SelfIdentityId == selfIdentityId.Value);
        if (existing is null)
        {
            _db.DirectSessions.Add(new DirectSessionDbo
            {
                RemotePeerId = remotePeerId.Value,
                SessionId = sessionId.Value,
                SelfIdentityId = selfIdentityId.Value
            });
        }
        else
        {
            existing.SessionId = sessionId.Value;
            existing.SelfIdentityId = selfIdentityId.Value;
            _db.DirectSessions.Update(existing);
        }
        await _db.SaveChangesAsync();
    }

    public async Task DeleteByRemotePeerIdAsync(Percolator.Network.PeerId remotePeerId, NetworkSelfId selfIdentityId)
    {
        var existing = await _db.DirectSessions
            .FirstOrDefaultAsync(x => x.RemotePeerId == remotePeerId.Value && x.SelfIdentityId == selfIdentityId.Value);
        if (existing != null)
        {
            _db.DirectSessions.Remove(existing);
            await _db.SaveChangesAsync();
        }
    }
}



