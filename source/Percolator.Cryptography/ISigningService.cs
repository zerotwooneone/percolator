using System.Security.Cryptography;

namespace Percolator.Cryptography;

public interface ISigningService
{
    Signature Sign(byte[] data, ECDsa privateKey);

    bool Verify(byte[] data, Signature signature, PublicKey publicKey);
}
