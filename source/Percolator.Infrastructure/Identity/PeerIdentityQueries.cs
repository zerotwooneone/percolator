using Microsoft.EntityFrameworkCore;
using Percolator.Application.Chat;
using Percolator.Cryptography;
using Percolator.Identity;
using Percolator.Infrastructure.Identity;
using Percolator.Infrastructure.Persistence;

namespace Percolator.Infrastructure.Identity;

public sealed class PeerIdentityQueries : IPeerIdentityQueries
{
    private readonly IDbContextFactory<PercolatorDbContext> _dbFactory;

    public PeerIdentityQueries(IDbContextFactory<PercolatorDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task<RatchetIdentityKey?> GetPublicKeyByPkhAsync(string senderPkh, CancellationToken ct)
    {
        using var db = _dbFactory.CreateDbContext();
        
        // Map the incoming string token challenge back to binary bytes
        var targetFingerprint = Convert.FromBase64String(senderPkh);
        
        var keyBytes = await db.PeerIdentities
            .AsNoTracking()
            .Join(
                db.PeerIdentityKeys_V2,
                peer => peer.PeerId,
                key => key.PeerId,
                (peer, key) => new { peer, key })
            .Where(x => x.key.Fingerprint == targetFingerprint)
            .Where(x => x.key.NotBeforeUtc <= DateTimeOffset.UtcNow)
            .Where(x => x.key.ExpiresAtUtc > DateTimeOffset.UtcNow)
            .Where(x => x.key.RevokedAtUtc == null)
            .Select(x => x.key.PublicKeySpki)
            .FirstOrDefaultAsync(ct);

        if (keyBytes is null)
        {
            return null;
        }

        return RatchetIdentityKey.FromBytes(keyBytes);
    }

    public async Task<PeerId?> GetPeerIdByPkhAsync(byte[] pkhBytes, CancellationToken ct)
    {
        using var db = _dbFactory.CreateDbContext();
        
        var peerId = await db.PeerIdentityKeys_V2
            .AsNoTracking()
            .Where(k => k.Fingerprint == pkhBytes)
            .Select(k => k.PeerId)
            .FirstOrDefaultAsync(ct);

        if (peerId == Guid.Empty)
        {
            return null;
        }

        return new PeerId(peerId);
    }
}
