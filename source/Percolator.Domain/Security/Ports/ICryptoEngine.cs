using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.ValueObjects;

namespace Percolator.Domain.Security.Ports;

public interface ICryptoEngine
{
    (ChainKey NextChainKey, MessageKey DerivedMessageKey) StepRatchet(ChainKey currentChainKey);
    (ChainKey NextRootKey, ChainKey DerivedChainKey) KdfRk(ChainKey currentRootKey, SharedSecret dhSecret);
    SharedSecret ComputeDiffieHellman(ReadOnlySpan<byte> privateKey, ReadOnlySpan<byte> publicKey);
    (byte[] PrivateKey, IdentityPublicKey PublicKey) GenerateEphemeralKeyPair();
    byte[] EncryptAesGcm(ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> associatedData);
    byte[] DecryptAesGcm(ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> associatedData);
    bool VerifyEd25519Signature(IdentityPublicKey publicKey, ReadOnlySpan<byte> message, ReadOnlySpan<byte> signature);
}
