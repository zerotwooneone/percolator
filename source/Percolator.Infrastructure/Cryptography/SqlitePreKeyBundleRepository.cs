using Microsoft.EntityFrameworkCore;
using Percolator.Cryptography;
using Percolator.Infrastructure.Persistence;
using CryptographyPeerId = Percolator.Cryptography.Primitives.PeerId;

namespace Percolator.Infrastructure.Cryptography;

public class SqlitePreKeyBundleRepository : IPreKeyBundleRepository
{
    private readonly PercolatorDbContext _context;

    public SqlitePreKeyBundleRepository(PercolatorDbContext context)
    {
        _context = context;
    }

    public async Task<PreKeyBundle?> PopBundleAsync(CryptographyPeerId peerId)
    {
        await using var transaction = await _context.Database.BeginTransactionAsync();

        try
        {
            var oneTimePreKey = await _context.OneTimePreKeys
                .FirstOrDefaultAsync(k => k.PeerIdentityKey.PeerId.Value == peerId.Value);

            var identityKeyDbo = await _context.PeerIdentityKeys
                .Include(ik => ik.SignedPreKeys)
                .Include(ik => ik.OneTimePreKeys)
                .FirstOrDefaultAsync(ik => ik.PeerId.Value == peerId.Value);

            if (identityKeyDbo is null)
            {
                await transaction.RollbackAsync();
                return null;
            }

            var signedPreKey = identityKeyDbo.SignedPreKeys.FirstOrDefault();
            if (signedPreKey is null)
            {
                await transaction.RollbackAsync();
                return null; // A bundle must have a signed pre-key
            }

            var bundle = new PreKeyBundle(
                new RatchetIdentityKey(identityKeyDbo.PublicKey),
                Guid.Parse(signedPreKey.Id),
                new PreKey(signedPreKey.PublicKey),
                new Signature(signedPreKey.Signature),
                oneTimePreKey is not null ? Guid.Parse(oneTimePreKey.Id): null,
                oneTimePreKey is not null ? new OneTimeKey(oneTimePreKey.PublicKey) : null
            );

            // Remove the used one-time key
            if (oneTimePreKey is not null)
            {
                _context.OneTimePreKeys.Remove(oneTimePreKey);
                await _context.SaveChangesAsync();
            }

            await transaction.CommitAsync();
            return bundle;
        }
        catch (Exception)
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

    public async Task StoreBundlesAsync(CryptographyPeerId peerId, IEnumerable<PreKeyBundle> bundles)
    {
        // Contract: replace any existing identity key and pre-keys with provided set atomically.
        // Aggregate: Use first bundle's IdentitySigningKey + SignedPreKey as canonical;
        // add all provided OneTimePreKeys (when present) from the sequence.
        if (bundles is null) throw new ArgumentNullException(nameof(bundles));
        var bundleList = bundles.ToList();
        if (bundleList.Count == 0) throw new InvalidOperationException("No pre-key bundles provided.")
;
        var first = bundleList[0];

        await using var transaction = await _context.Database.BeginTransactionAsync();
        try
        {
            // EF Sqlite struggles to translate value-object property access (p.Id.Value) in queries.
            // Materialize peers then compare by Guid locally to avoid translation issues.
            var peers = await _context.Peers.AsNoTracking().ToListAsync();
            var peer = peers.FirstOrDefault(p => p.Id.Value == peerId.Value);
            if (peer is null)
            {
                throw new InvalidOperationException($"Peer {peerId} not found.");
            }

            // Remove any existing identity key and its related pre-keys for this peer
            var identityKeys = await _context.PeerIdentityKeys
                .Include(ik => ik.SignedPreKeys)
                .Include(ik => ik.OneTimePreKeys)
                .AsNoTracking()
                .ToListAsync();
            var existing = identityKeys.FirstOrDefault(ik => ik.PeerId.Value == peerId.Value);

            if (existing is not null)
            {
                _context.PeerIdentityKeys.Remove(existing); // required FKs will cascade delete children
                await _context.SaveChangesAsync();
            }

            var identityKey = new PeerIdentityKeyDbo
            {
                PeerId = peer.Id,
                PublicKey = first.IdentitySigningKey.Value,
            };

            // Add the canonical signed pre-key from the first bundle
            identityKey.SignedPreKeys.Add(new SignedPreKeyDbo
            {
                Id = first.SignedPreKeyId.ToString(),
                PublicKey = first.SignedPreKey.Value,
                Signature = first.SignedPreKeySignature.Value,
            });

            // Add all one-time pre-keys present across bundles
            foreach (var b in bundleList)
            {
                if (b.OneTimePreKey is null || b.OneTimePreKeyId is null) continue;
                identityKey.OneTimePreKeys.Add(new OneTimePreKeyDbo
                {
                    Id = b.OneTimePreKeyId.ToString(),
                    PublicKey = b.OneTimePreKey.Value,
                });
            }

            _context.PeerIdentityKeys.Add(identityKey);
            await _context.SaveChangesAsync();
            await transaction.CommitAsync();
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }
}

