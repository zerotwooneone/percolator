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
                .FirstOrDefaultAsync(ik => ik.Peer.Id.Value == peerId.Value);

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
        // Since we are moving to a hierarchical model, we expect one 'bundle' which contains all the keys.
        var bundle = bundles.Single();

        var peer = await _context.Peers.FirstOrDefaultAsync(p => p.Id.Value == peerId.Value);
        if (peer is null)
        {
            throw new InvalidOperationException($"Peer {peerId} not found.");
        }

        var identityKey = new PeerIdentityKeyDbo
        {
            Peer = peer,
            PublicKey = bundle.IdentitySigningKey.Value,
        };

        // The current PreKeyBundle doesn't support multiple signed/one-time keys.
        // We'll add the single signed pre-key and the optional one-time pre-key.
        identityKey.SignedPreKeys.Add(new SignedPreKeyDbo
        {
            Id = bundle.SignedPreKeyId.ToString(),
            PublicKey = bundle.SignedPreKey.Value,
            Signature = bundle.SignedPreKeySignature.Value,
        });

        if (bundle.OneTimePreKey is not null && bundle.OneTimePreKeyId is not null)
        {
            identityKey.OneTimePreKeys.Add(new OneTimePreKeyDbo
            {
                Id = bundle.OneTimePreKeyId.ToString(),
                PublicKey = bundle.OneTimePreKey.Value,
            });
        }

        _context.PeerIdentityKeys.Add(identityKey);
        await _context.SaveChangesAsync();
    }
}
