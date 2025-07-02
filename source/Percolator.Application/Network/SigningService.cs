using Percolator.Application.Identity;
using Percolator.Network;
using System.Security.Cryptography;
using Crypto = Percolator.Cryptography;

namespace Percolator.Application.Network;

public class SigningService : ISigningService
{
    private readonly ActiveIdentityContext _activeIdentityContext;
    private readonly Crypto.ISigningService _cryptographyService;

    public SigningService(ActiveIdentityContext activeIdentityContext, Crypto.ISigningService cryptographyService)
    {
        _activeIdentityContext = activeIdentityContext;
        _cryptographyService = cryptographyService;
    }

    private ECDsa GetActiveSigningKey()
    {
        if (_activeIdentityContext.Keys?.IdentitySigningKey is null)
        {
            throw new InvalidOperationException("Cannot perform signing operation: Active identity is not loaded.");
        }
        return _activeIdentityContext.Keys.IdentitySigningKey;
    }

    public PublicKey GetActivePublicKey()
    {
        var privateKey = GetActiveSigningKey();
        var publicKeyBytes = privateKey.ExportSubjectPublicKeyInfo();
        return new PublicKey(publicKeyBytes);
    }

    public PublicKeyHash GetActivePublicKeyHash()
    {
        var publicKey = GetActivePublicKey();
        return GetHash(publicKey);
    }

    public PublicKeyHash GetHash(PublicKey publicKey)
    {
        var hash = SHA256.HashData(publicKey.Value);
        return new PublicKeyHash(hash);
    }

    public Signature Sign(Payload payload)
    {
        var privateKey = GetActiveSigningKey();
        var cryptoSignature = _cryptographyService.Sign(payload.Value, privateKey);
        return new Signature(cryptoSignature.Value);
    }

    public bool Verify(Payload payload, Signature signature, PublicKey publicKey)
    {
        var cryptoSignature = new Crypto.Signature(signature.Value);
        var cryptoPublicKey = new Crypto.PublicKey(publicKey.Value);
        return _cryptographyService.Verify(payload.Value, cryptoSignature, cryptoPublicKey);
    }
}
