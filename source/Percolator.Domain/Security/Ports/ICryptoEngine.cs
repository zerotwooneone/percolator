using Percolator.Domain.Security.ValueObjects;

namespace Percolator.Domain.Security.Ports;

public interface ICryptoEngine
{
    (ChainKey NextChainKey, MessageKey DerivedMessageKey) StepRatchet(ChainKey currentChainKey);
    SharedSecret ComputeDiffieHellman(ReadOnlySpan<byte> privateKey, ReadOnlySpan<byte> publicKey);
    byte[] EncryptAesGcm(ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> associatedData);
    byte[] DecryptAesGcm(ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> associatedData);
}
