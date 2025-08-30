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

    public async Task<DirectSession?> GetBySessionIdAsync(DirectSessionId sessionId)
    {
        var dbo = await _db.DirectSessions.AsNoTracking().FirstOrDefaultAsync(x => x.SessionId == sessionId.Value);
        return dbo is null ? null : new DirectSession(new PeerId(dbo.RemotePeerId.Value), new DirectSessionId(dbo.SessionId));
    }

    public async Task<DirectSession?> GetByRemotePeerIdAsync(PeerId remotePeerId)
    {
        var dbo = await _db.DirectSessions.AsNoTracking().FirstOrDefaultAsync(x => x.RemotePeerId == new Percolator.Identity.PeerId(remotePeerId.Value));
        return dbo is null ? null : new DirectSession(new PeerId(dbo.RemotePeerId.Value), new DirectSessionId(dbo.SessionId));
    }

    public async Task UpsertAsync(PeerId remotePeerId, DirectSessionId sessionId)
    {
        var existing = await _db.DirectSessions.FirstOrDefaultAsync(x => x.RemotePeerId == new Percolator.Identity.PeerId(remotePeerId.Value));
        if (existing is null)
        {
            _db.DirectSessions.Add(new DirectSessionDbo
            {
                RemotePeerId = new Percolator.Identity.PeerId(remotePeerId.Value),
                SessionId = sessionId.Value
            });
        }
        else
        {
            existing.SessionId = sessionId.Value;
            _db.DirectSessions.Update(existing);
        }
        await _db.SaveChangesAsync();
    }

    public async Task DeleteByRemotePeerIdAsync(PeerId remotePeerId)
    {
        var existing = await _db.DirectSessions.FirstOrDefaultAsync(x => x.RemotePeerId == new Percolator.Identity.PeerId(remotePeerId.Value));
        if (existing != null)
        {
            _db.DirectSessions.Remove(existing);
            await _db.SaveChangesAsync();
        }
    }
}

