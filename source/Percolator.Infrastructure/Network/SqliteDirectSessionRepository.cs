using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
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

    public async Task<DirectSession?> GetBySessionIdAsync(DirectSessionId sessionId, int selfIdentityId)
    {
        var dbo = await _db.DirectSessions
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.SessionId == sessionId.Value && x.SelfIdentityId == selfIdentityId);
        return dbo is null ? null : new DirectSession(new PeerId(dbo.RemotePeerId), new DirectSessionId(dbo.SessionId));
    }

    public async Task<DirectSession?> GetByRemotePeerIdAsync(PeerId remotePeerId, int selfIdentityId)
    {
        var dbo = await _db.DirectSessions
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.RemotePeerId == remotePeerId.Value && x.SelfIdentityId == selfIdentityId);
        return dbo is null ? null : new DirectSession(new PeerId(dbo.RemotePeerId), new DirectSessionId(dbo.SessionId));
    }

    public async Task UpsertAsync(PeerId remotePeerId, DirectSessionId sessionId, int selfIdentityId)
    {
        var existing = await _db.DirectSessions
            .FirstOrDefaultAsync(x => x.RemotePeerId == remotePeerId.Value && x.SelfIdentityId == selfIdentityId);
        if (existing is null)
        {
            _db.DirectSessions.Add(new DirectSessionDbo
            {
                RemotePeerId = remotePeerId.Value,
                SessionId = sessionId.Value,
                SelfIdentityId = selfIdentityId
            });
        }
        else
        {
            existing.SessionId = sessionId.Value;
            existing.SelfIdentityId = selfIdentityId;
            _db.DirectSessions.Update(existing);
        }
        await _db.SaveChangesAsync();
    }

    public async Task DeleteByRemotePeerIdAsync(PeerId remotePeerId, int selfIdentityId)
    {
        var existing = await _db.DirectSessions
            .FirstOrDefaultAsync(x => x.RemotePeerId == remotePeerId.Value && x.SelfIdentityId == selfIdentityId);
        if (existing != null)
        {
            _db.DirectSessions.Remove(existing);
            await _db.SaveChangesAsync();
        }
    }
}



