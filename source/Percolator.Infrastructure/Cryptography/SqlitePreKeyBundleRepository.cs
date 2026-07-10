using Microsoft.EntityFrameworkCore;
using Percolator.Cryptography;
using Percolator.Cryptography.Primitives;
using Percolator.Infrastructure.Persistence;

namespace Percolator.Infrastructure.Cryptography;

public class SqlitePreKeyBundleRepository : IPreKeyBundleRepository
{
    private readonly PercolatorDbContext _context;

    public SqlitePreKeyBundleRepository(PercolatorDbContext context)
    {
        _context = context;
    }

    public async Task<PreKeyBundle?> PopBundleAsync(CryptoPeerId cryptoPeerId)
    {
        await using var transaction = await _context.Database.BeginTransactionAsync();

        try
        {
            // Load identity + children in memory, then filter locally to avoid Sqlite translation issues
            var preKeyBundleDbos = await _context.PreKeyBundles
                .Include(ik => ik.SignedPreKeys)
                .Include(ik => ik.OneTimePreKeys)
                .ToListAsync();
            var preKeyBundle = preKeyBundleDbos.FirstOrDefault(ik => ik.PeerId == cryptoPeerId.Value);

            if (preKeyBundle is null)
            {
                await transaction.RollbackAsync();
                return null;
            }

            var signedPreKey = preKeyBundle.SignedPreKeys.FirstOrDefault();
            if (signedPreKey is null)
            {
                await transaction.RollbackAsync();
                return null; // A bundle must have a signed pre-key
            }

            var oneTimePreKey = preKeyBundle.OneTimePreKeys.FirstOrDefault();

            var bundle = new PreKeyBundle(
                RatchetIdentityKey.FromBytesOwned(preKeyBundle.PublicKey),
                Guid.Parse(signedPreKey.Id),
                PreKey.FromBytesOwned(signedPreKey.PublicKey),
                Signature.FromBytesOwned(signedPreKey.Signature),
                oneTimePreKey is not null ? Guid.Parse(oneTimePreKey.Id) : null,
                oneTimePreKey is not null ? OneTimeKey.FromBytesOwned(oneTimePreKey.PublicKey) : null
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

    public async Task StoreBundlesAsync(CryptoPeerId cryptoPeerId, IEnumerable<PreKeyBundle> bundles)
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
            // Resolve peer existence via authoritative PeerIdentities catalog
            var identity = await _context.PeerIdentities.AsNoTracking()
                .FirstOrDefaultAsync(pi => pi.PeerId == cryptoPeerId.Value);
            if (identity is null)
            {
                throw new InvalidOperationException($"Peer {cryptoPeerId} not found.");
            }

            // Remove any existing identity key and its related pre-keys for this peer
            var preKeyBundleDbos = await _context.PreKeyBundles
                .Include(ik => ik.SignedPreKeys)
                .Include(ik => ik.OneTimePreKeys)
                .AsNoTracking()
                .ToListAsync();
            var existing = preKeyBundleDbos.FirstOrDefault(ik => ik.PeerId == cryptoPeerId.Value);

            if (existing is not null)
            {
                _context.PreKeyBundles.Remove(existing); // required FKs will cascade delete children
                await _context.SaveChangesAsync();
            }

            var preKeyBundle = new PreKeyBundleDbo
            {
                PeerId = cryptoPeerId.Value,
                PublicKey = first.IdentitySigningKey.ToArray(),
            };

            // Add the canonical signed pre-key from the first bundle
            preKeyBundle.SignedPreKeys.Add(new SignedPreKeyDbo
            {
                Id = first.SignedPreKeyId.ToString(),
                PublicKey = first.SignedPreKey.ToArray(),
                Signature = first.SignedPreKeySignature.ToArray(),
            });

            // Add all one-time pre-keys present across bundles
            foreach (var b in bundleList)
            {
                if (b.OneTimePreKey is null || b.OneTimePreKeyId is null) continue;
                preKeyBundle.OneTimePreKeys.Add(new OneTimePreKeyDbo
                {
                    Id = b.OneTimePreKeyId.ToString(),
                    PublicKey = b.OneTimePreKey.ToArray(),
                });
            }

            _context.PreKeyBundles.Add(preKeyBundle);
            await _context.SaveChangesAsync();
            await transaction.CommitAsync();
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

    public async Task<PreKeyBundle?> TryPopBundleAsync(CryptoPeerId cryptoPeerId, Guid signedPreKeyId, Guid? oneTimePreKeyId)
    {
        await using var transaction = await _context.Database.BeginTransactionAsync();

        try
        {
            // Load identity + keys for this peer
            var preKeyBundleDbo = await _context.PreKeyBundles
                .Include(ik => ik.SignedPreKeys)
                .Include(ik => ik.OneTimePreKeys)
                .FirstOrDefaultAsync(ik => ik.PeerId == cryptoPeerId.Value);

            if (preKeyBundleDbo is null)
            {
                await transaction.RollbackAsync();
                return null;
            }

            var spk = preKeyBundleDbo.SignedPreKeys.FirstOrDefault(k => k.Id == signedPreKeyId.ToString());
            if (spk is null)
            {
                await transaction.RollbackAsync();
                return null; // signed pre-key id mismatch
            }

            OneTimePreKeyDbo? otkDbo;
            if (oneTimePreKeyId.HasValue)
            {
                var otkId = oneTimePreKeyId.Value.ToString();
                otkDbo = preKeyBundleDbo.OneTimePreKeys.FirstOrDefault(k => k.Id == otkId);
                if (otkDbo is null)
                {
                    await transaction.RollbackAsync();
                    return null; // requested OTK not available
                }
            }
            else
            {
                otkDbo = preKeyBundleDbo.OneTimePreKeys.FirstOrDefault();
            }

            var bundle = new PreKeyBundle(
                RatchetIdentityKey.FromBytesOwned(preKeyBundleDbo.PublicKey),
                signedPreKeyId,
                PreKey.FromBytesOwned(spk.PublicKey),
                Signature.FromBytesOwned(spk.Signature),
                otkDbo is not null ? Guid.Parse(otkDbo.Id) : null,
                otkDbo is not null ? OneTimeKey.FromBytesOwned(otkDbo.PublicKey) : null
            );

            // Remove the used one-time key if any
            if (otkDbo is not null)
            {
                _context.OneTimePreKeys.Remove(otkDbo);
                await _context.SaveChangesAsync();
            }

            await transaction.CommitAsync();
            return bundle;
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }
}

