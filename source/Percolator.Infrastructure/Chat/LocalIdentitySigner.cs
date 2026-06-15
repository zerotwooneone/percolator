using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Percolator.Application.Chat;
using Percolator.Cryptography;
using Percolator.Infrastructure.Persistence;

namespace Percolator.Infrastructure.Chat;

public sealed class LocalIdentitySigner : ILocalIdentitySigner
{
    private readonly IDbContextFactory<PercolatorDbContext> _dbFactory;
    private readonly ILogger<LocalIdentitySigner> _logger;

    public LocalIdentitySigner(
        IDbContextFactory<PercolatorDbContext> dbFactory,
        ILogger<LocalIdentitySigner> logger)
    {
        _dbFactory = dbFactory;
        _logger = logger;
    }

    public async Task<Signature> SignWithRelayRootKeyAsync(byte[] payload, CancellationToken ct)
    {
        using var db = _dbFactory.CreateDbContext();
        
        var relayRootKeyBytes = await db.SelfIdentities
            .AsNoTracking()
            .Where(s => s.RelayDeliveryRootKey != null)
            .Select(s => s.RelayDeliveryRootKey)
            .FirstOrDefaultAsync(ct);

        if (relayRootKeyBytes is null)
        {
            throw new InvalidOperationException("Relay root key not found. Relay mode may not be enabled.");
        }

        var relayRootKey = RelayRootKeyBytes.FromBytesOwned(relayRootKeyBytes);
        
        var signature = new byte[Signal.Interop.SignalCrypto.Ed25519SignatureLength];
        Signal.Interop.SignalCrypto.Ed25519Sign(
            relayRootKey.Span,
            payload,
            signature);
        
        return Signature.FromBytesOwned(signature);
    }

    public async Task<Signature> SignWithLocalIdentityKeyAsync(byte[] payload, CancellationToken ct)
    {
        using var db = _dbFactory.CreateDbContext();
        
        var identityKeyBytes = await db.SelfIdentities
            .AsNoTracking()
            .Include(s => s.Keys)
            .Where(s => s.Keys != null)
            .Select(s => s.Keys.IdentitySigningKey)
            .FirstOrDefaultAsync(ct);

        if (identityKeyBytes is null)
        {
            throw new InvalidOperationException("Local identity signing key not found.");
        }

        // The IdentitySigningKey is stored as ECParameters (ECDsa nistP256)
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportECPrivateKey(identityKeyBytes, out _);
        
        var signatureBytes = ecdsa.SignData(payload, HashAlgorithmName.SHA256);
        
        return Signature.FromBytesOwned(signatureBytes);
    }
}
