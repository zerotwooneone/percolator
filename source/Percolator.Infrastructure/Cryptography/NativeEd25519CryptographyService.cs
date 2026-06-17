using Signal.Interop;
using Percolator.Cryptography;

namespace Percolator.Infrastructure.Cryptography;

public sealed class NativeEd25519CryptographyService : IEd25519CryptographyService
{
    public void GenerateKeyPair(out RelayRootKeyBytes privateKey, out Ed25519PublicKeyBytes publicKey)
    {
        byte[] privateKeyBytes = new byte[SignalCrypto.Ed25519PrivateKeyLength];
        byte[] publicKeyBytes = new byte[SignalCrypto.Ed25519PublicKeyLength];

        SignalCrypto.GenerateEd25519KeyPair(privateKeyBytes, publicKeyBytes);

        privateKey = RelayRootKeyBytes.FromBytesOwned(privateKeyBytes);
        publicKey = Ed25519PublicKeyBytes.FromBytesOwned(publicKeyBytes);
    }

    public Ed25519SignatureBytes Sign(ReadOnlySpan<byte> message, RelayRootKeyBytes privateKey)
    {
        byte[] signatureBytes = new byte[SignalCrypto.Ed25519SignatureLength];

        SignalCrypto.Ed25519Sign(privateKey.Span, message, signatureBytes);

        return Ed25519SignatureBytes.FromBytesOwned(signatureBytes);
    }

    public bool Verify(Ed25519PublicKeyBytes publicKey, ReadOnlySpan<byte> message, Ed25519SignatureBytes signature)
    {
        return SignalCrypto.Ed25519Verify(publicKey.Span, message, signature.Span);
    }
}
