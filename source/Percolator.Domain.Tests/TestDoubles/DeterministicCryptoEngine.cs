using System.Security.Cryptography;
using Percolator.Domain.Security.Ports;
using Percolator.Domain.Security.ValueObjects;

namespace Percolator.Domain.Tests.TestDoubles;

public sealed class DeterministicCryptoEngine : ICryptoEngine
{
    public (ChainKey NextChainKey, MessageKey DerivedMessageKey) StepRatchet(ChainKey currentChainKey)
    {
        Span<byte> nextChainBytes = stackalloc byte[32];
        Span<byte> messageKeyBytes = stackalloc byte[32];

        // Deterministic derivation: SHA256(currentChainKey || 0x01) and SHA256(currentChainKey || 0x02)
        Span<byte> buffer = stackalloc byte[33];
        currentChainKey.Span.CopyTo(buffer);

        buffer[32] = 0x01;
        SHA256.HashData(buffer, nextChainBytes);

        buffer[32] = 0x02;
        SHA256.HashData(buffer, messageKeyBytes);

        return (ChainKey.FromSpan(nextChainBytes), MessageKey.FromSpan(messageKeyBytes));
    }

    public SharedSecret ComputeDiffieHellman(ReadOnlySpan<byte> privateKey, ReadOnlySpan<byte> publicKey)
    {
        Span<byte> hash = stackalloc byte[32];
        Span<byte> combined = stackalloc byte[privateKey.Length + publicKey.Length];
        privateKey.CopyTo(combined);
        publicKey.CopyTo(combined[privateKey.Length..]);
        SHA256.HashData(combined, hash);
        return SharedSecret.FromSpan(hash);
    }

    public byte[] EncryptAesGcm(ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> associatedData)
    {
        // Simple deterministic test mock: plaintext prefixed with nonce
        var result = new byte[plaintext.Length + nonce.Length];
        nonce.CopyTo(result);
        plaintext.CopyTo(result.AsSpan(nonce.Length));
        return result;
    }

    public byte[] DecryptAesGcm(ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> associatedData)
    {
        if (ciphertext.Length < nonce.Length) return [];
        return ciphertext[nonce.Length..].ToArray();
    }
}
