using System.Security.Cryptography;

namespace Percolator.Cryptography;

public static class CryptographyExtensions
{
    public static ECDiffieHellmanPublicKey ToEcdhPublicKey(this RatchetIdentityKey publicKey)
    {
        using var ecdh = ECDiffieHellman.Create();
        ecdh.ImportSubjectPublicKeyInfo(publicKey.Value, out _);
        return ecdh.PublicKey;
    }
    
    public static ECDiffieHellmanPublicKey ToEcdhPublicKey(this RatchetEphemeralKey publicKey)
    {
        using var ecdh = ECDiffieHellman.Create();
        ecdh.ImportSubjectPublicKeyInfo(publicKey.Value, out _);
        return ecdh.PublicKey;
    }
    
    public static ECDiffieHellmanPublicKey ToEcdhPublicKey(this PreKey publicKey)
    {
        using var ecdh = ECDiffieHellman.Create();
        ecdh.ImportSubjectPublicKeyInfo(publicKey.Value, out _);
        return ecdh.PublicKey;
    }
    
    public static ECDiffieHellmanPublicKey ToEcdhPublicKey(this RatchetAgreementKey publicKey)
    {
        using var ecdh = ECDiffieHellman.Create();
        ecdh.ImportSubjectPublicKeyInfo(publicKey.Value, out _);
        return ecdh.PublicKey;
    }
    
    public static ECDiffieHellmanPublicKey ToEcdhPublicKey(this OneTimeKey publicKey)
    {
        using var ecdh = ECDiffieHellman.Create();
        ecdh.ImportSubjectPublicKeyInfo(publicKey.Value, out _);
        return ecdh.PublicKey;
    }
}
