using System.Security.Cryptography;

namespace Percolator.Cryptography;

public class EcdsaSigningService : ISigningService
{
    public Signature Sign(byte[] data, ECDsa privateKey)
    {
        var signature = privateKey.SignData(data, HashAlgorithmName.SHA256);
        return new Signature(signature);
    }

    public bool Verify(byte[] data, Signature signature, PublicKey publicKey)
    {
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportSubjectPublicKeyInfo(publicKey.Value, out _);
        return ecdsa.VerifyData(data, signature.Value, HashAlgorithmName.SHA256);
    }
}
