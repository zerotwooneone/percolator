namespace Percolator.Cryptography;

public interface IEd25519CryptographyService
{
    void GenerateKeyPair(out RelayRootKeyBytes privateKey, out Ed25519PublicKeyBytes publicKey);
    Ed25519SignatureBytes Sign(ReadOnlySpan<byte> message, RelayRootKeyBytes privateKey);
    bool Verify(Ed25519PublicKeyBytes publicKey, ReadOnlySpan<byte> message, Ed25519SignatureBytes signature);
}
