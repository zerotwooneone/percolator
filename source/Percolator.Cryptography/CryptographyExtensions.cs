using System.Security.Cryptography;

namespace Percolator.Cryptography;

public static class CryptographyExtensions
{
    public static ECDiffieHellmanPublicKey ToEcdhPublicKey(this PublicKey publicKey)
    {
        using var ecdh = ECDiffieHellman.Create();
        ecdh.ImportSubjectPublicKeyInfo(publicKey.Value, out _);
        return ecdh.PublicKey;
    }
}
