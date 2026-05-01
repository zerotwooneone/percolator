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
        if (bundle.IdentitySigningKey is null || bundle.IdentitySigningKey.Span.Length == 0)
            throw new CryptographicException("identity signing key missing");
        if (bundle.SignedPreKey is null || bundle.SignedPreKey.Span.Length == 0)
            throw new CryptographicException("signed pre-key missing");
        if (bundle.SignedPreKeySignature is null || bundle.SignedPreKeySignature.Span.Length == 0)
            throw new CryptographicException("signed pre-key signature missing");

        // Verify SPK signature
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportSubjectPublicKeyInfo(bundle.IdentitySigningKey.Span, out _);
        var ok = ecdsa.VerifyData(bundle.SignedPreKey.Span, bundle.SignedPreKeySignature.Span, HashAlgorithmName.SHA256);
        if (!ok) throw new CryptographicException("signed pre-key signature invalid");
    }
}
