using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.ValueObjects;

namespace Percolator.Domain.Security.Ports;

public interface ICryptoEngine
{
    (ChainKey NextChainKey, MessageKey DerivedMessageKey) StepRatchet(ChainKey currentChainKey);
    (ChainKey NextRootKey, ChainKey DerivedChainKey) KdfRk(ChainKey currentRootKey, SharedSecret dhSecret);
    SharedSecret ComputeDiffieHellman(ReadOnlySpan<byte> privateKey, ReadOnlySpan<byte> publicKey);
    (EphemeralPrivateKey PrivateKey, DhPublicKey PublicKey) GenerateEphemeralKeyPair();
    SharedSecret DeriveX3dhMasterSecret(ReadOnlySpan<byte> dh1, ReadOnlySpan<byte> dh2, ReadOnlySpan<byte> dh3, ReadOnlySpan<byte> dh4 = default);
    byte[] EncryptAesGcm(ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> associatedData);
    byte[] DecryptAesGcm(ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> associatedData);
    bool VerifyEd25519Signature(IdentityKey publicKey, ReadOnlySpan<byte> message, ReadOnlySpan<byte> signature);
    byte[] SignEd25519(ReadOnlySpan<byte> privateKey, ReadOnlySpan<byte> message);
}
