using System.Security.Cryptography;

namespace Percolator.Cryptography;

public interface IEd25519CryptographyService
{
    Ed25519SignatureBytes Sign(byte[] message, ECDiffieHellman privateKey);
    bool Verify(Ed25519PublicKeyBytes publicKey, byte[] message, Ed25519SignatureBytes signature);
}
