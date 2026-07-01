using Microsoft.EntityFrameworkCore;
using Percolator.Application.Chat;
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

    public async Task<RatchetIdentityKey?> GetPublicKeyByPkhAsync(IdentityPublicKeyHash senderPkh, CancellationToken ct)
    {
        using var db = _dbFactory.CreateDbContext();
        
        var candidateKeys = await db.PeerIdentities
            .AsNoTracking()
            .Join(
                db.PeerIdentityKeys,
                peer => peer.PeerId,
                key => key.PeerId,
                (peer, key) => new { peer, key })
            .Where(x => x.key.NotBeforeUtc <= DateTimeOffset.UtcNow)
            .Where(x => x.key.ExpiresAtUtc > DateTimeOffset.UtcNow)
            .Where(x => x.key.RevokedAtUtc == null)
            .Select(x => x.key.PublicKeySpki)
            .ToListAsync(ct);

        var keyBytes = candidateKeys.FirstOrDefault(x => senderPkh.Span.SequenceEqual(x));

        if (keyBytes is null)
        {
            return null;
        }

        return RatchetIdentityKey.FromBytesOwned(keyBytes);
    }

    public async Task<PeerId?> GetPeerIdByPkhAsync(IdentityPublicKeyHash pkh, CancellationToken ct)
    {
        using var db = _dbFactory.CreateDbContext();
        
        var activeKeys = await db.PeerIdentityKeys
            .AsNoTracking()
            .Where(x => x.NotBeforeUtc <= DateTimeOffset.UtcNow)
            .Where(x => x.ExpiresAtUtc > DateTimeOffset.UtcNow)
            .Where(x => x.RevokedAtUtc == null)
            .Select(x => new { x.PeerId, x.Fingerprint })
            .ToListAsync(ct);

        var match = activeKeys.FirstOrDefault(x => pkh.Span.SequenceEqual(x.Fingerprint));

        if (match is null)
        {
            return null;
        }

        return match.PeerId;
    }

    public async Task<Pkh?> GetPublicKeyHashAsync(PeerId peerId, CancellationToken cancellationToken)
    {
        using var db = _dbFactory.CreateDbContext();
        var keyBytes = await db.PeerIdentityKeys
            .AsNoTracking()
            .Where(x => x.PeerId == peerId)
            .Where(x => x.NotBeforeUtc <= DateTimeOffset.UtcNow)
            .Where(x => x.ExpiresAtUtc > DateTimeOffset.UtcNow)
            .Where(x => x.RevokedAtUtc == null)
            .Select(x => x.Fingerprint)
            .FirstOrDefaultAsync(cancellationToken);
        return keyBytes is null ? null : Pkh.FromBytesOwned(keyBytes);
    }
}
