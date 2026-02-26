using System.Security.Cryptography;

namespace Percolator.Cryptography;

public class InMemoryOneTimeKeyProvider : IOneTimeKeyProvider
{
    public (OneTimeKey publicKey, PrivateOneTimeKey privateKey)? PopOneTimeKey()
    {
        using var key = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var spki = key.PublicKey.ExportSubjectPublicKeyInfo();
        var privateKey = key.ExportECPrivateKey();
        return new ValueTuple<OneTimeKey, PrivateOneTimeKey>(new OneTimeKey(spki), new PrivateOneTimeKey(privateKey));
    }
}
