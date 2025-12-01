using System.Security.Cryptography;

namespace Percolator.Cryptography;

public interface IPreKeyBundleValidator
{
    void Validate(PreKeyBundle bundle);
}

public sealed class PreKeyBundleValidator : IPreKeyBundleValidator
{
    public void Validate(PreKeyBundle bundle)
    {
        if (bundle is null) throw new ArgumentNullException(nameof(bundle));
        if (bundle.ExpirationDateUtc.HasValue && bundle.ExpirationDateUtc.Value <= DateTimeOffset.UtcNow)
            throw new CryptographicException("pre-key bundle expired");
        if (bundle.IdentitySigningKey?.Value is null || bundle.IdentitySigningKey.Value.Length == 0)
            throw new CryptographicException("identity signing key missing");
        if (bundle.SignedPreKey?.Value is null || bundle.SignedPreKey.Value.Length == 0)
            throw new CryptographicException("signed pre-key missing");
        if (bundle.SignedPreKeySignature?.Value is null || bundle.SignedPreKeySignature.Value.Length == 0)
            throw new CryptographicException("signed pre-key signature missing");

        // Verify SPK signature
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportSubjectPublicKeyInfo(bundle.IdentitySigningKey.Value, out _);
        var ok = ecdsa.VerifyData(bundle.SignedPreKey.Value, bundle.SignedPreKeySignature.Value, HashAlgorithmName.SHA256);
        if (!ok) throw new CryptographicException("signed pre-key signature invalid");
    }
}
