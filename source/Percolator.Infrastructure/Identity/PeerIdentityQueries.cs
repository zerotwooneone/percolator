using Microsoft.EntityFrameworkCore;
using Percolator.Application.Chat;
using Percolator.Chat.GroupMembership;
using Percolator.Chat.Messaging.ValueObjects;
using Percolator.Cryptography;
using Percolator.Identity;
using Percolator.Infrastructure.Persistence;

namespace Percolator.Infrastructure.Identity;

public sealed class PeerIdentityQueries : IPeerIdentityQueries
{
    private readonly IDbContextFactory<PercolatorDbContext> _dbFactory;

    public PeerIdentityQueries(IDbContextFactory<PercolatorDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

   public async Task<RatchetIdentityKey?> GetPublicKeyByPublicIdentityIdAsync(PublicIdentityId publicIdentityId, CancellationToken ct)
    {
        using var db = _dbFactory.CreateDbContext();
        
        var candidateKeys = await db.PeerIdentities
            .AsNoTracking()
            .Join(
                db.PeerIdentityKeys,
                peer => peer.PeerId,
                key => key.PeerId,
                (peer, key) => new { peer, key })
            .Where(x => x.peer.PublicIdentityId == publicIdentityId.Value)
            .Where(x => x.key.NotBeforeUtc <= DateTimeOffset.UtcNow)
            .Where(x => x.key.ExpiresAtUtc > DateTimeOffset.UtcNow)
            .Where(x => x.key.RevokedAtUtc == null)
            .Select(x => x.key.PublicKeySpki)
            .ToListAsync(ct);

        var keyBytes = candidateKeys.FirstOrDefault();

        if (keyBytes is null)
        {
            return null;
        }

        return RatchetIdentityKey.FromBytesOwned(keyBytes);
    }
    

    public async Task<PeerId?> GetPeerIdByPublicIdentityIdAsync(PublicIdentityId publicIdentityId, CancellationToken ct)
    {
        using var db = _dbFactory.CreateDbContext();
        
        var peerId = await db.PeerIdentities
            .AsNoTracking()
            .Where(x => x.PublicIdentityId == publicIdentityId.Value)
            .Select(x => x.PeerId)
            .FirstOrDefaultAsync(ct);

        if (peerId == 0)
        {
            return null;
        }

        return new PeerId(peerId);
    }

    public async Task<Percolator.Identity.PublicIdentityId?> GetPublicIdentityIdAsync(PeerId senderPeerId, CancellationToken cancellationToken)
    {
        using var db = _dbFactory.CreateDbContext();

        var publicIdentityId = await db.PeerIdentities
            .AsNoTracking()
            .Where(pid => pid.PeerId == senderPeerId.Value)
            .Select(pid=>pid.PublicIdentityId)
            .FirstOrDefaultAsync();

        return publicIdentityId == Guid.Empty ? null : new PublicIdentityId(publicIdentityId);
    }

    public async Task<PeerOrSelfId?> GetPeerOrSelfIdByPublicIdentityIdAsync(PublicIdentityId publicIdentityId, CancellationToken ct)
    {
        using var db = _dbFactory.CreateDbContext();

        // First check if it's a local self identity
        var selfIdentity = await db.SelfIdentities
            .AsNoTracking()
            .Where(s => s.PublicIdentityId == publicIdentityId.Value)
            .Select(s => s.Id)
            .FirstOrDefaultAsync(ct);

        if (selfIdentity != 0)
        {
            return PeerOrSelfId.FromSelfId(new ChatSelfId(selfIdentity));
        }

        // Otherwise check if it's a remote peer
        var peerId = await db.PeerIdentities
            .AsNoTracking()
            .Where(p => p.PublicIdentityId == publicIdentityId.Value)
            .Select(p => p.PeerId)
            .FirstOrDefaultAsync(ct);

        if (peerId != 0)
        {
            return PeerOrSelfId.FromPeerId(new PeerId(peerId));
        }

        return null;
    }
}
