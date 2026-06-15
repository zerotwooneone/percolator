using System.Security.Cryptography;

namespace Percolator.Cryptography;

public sealed class Ed25519CryptographyService : IEd25519CryptographyService
{
    public Ed25519SignatureBytes Sign(byte[] message, ECDiffieHellman privateKey)
    {
        var ecdsa = ECDsa.Create(privateKey.ExportParameters(true));
        var signature = ecdsa.SignData(message, HashAlgorithmName.SHA256);
        return Ed25519SignatureBytes.FromBytesOwned(signature);
    }

    public bool Verify(Ed25519PublicKeyBytes publicKey, byte[] message, Ed25519SignatureBytes signature)
    {
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportSubjectPublicKeyInfo(publicKey.Span, out _);
        return ecdsa.VerifyData(message, signature.Span, HashAlgorithmName.SHA256);
    }
}
