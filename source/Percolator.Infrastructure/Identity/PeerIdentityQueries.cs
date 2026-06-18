using Microsoft.EntityFrameworkCore;
using Percolator.Application.Chat;
using Percolator.Cryptography;
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

        return RatchetIdentityKey.FromBytesOwned(keyBytes);
    }
}
