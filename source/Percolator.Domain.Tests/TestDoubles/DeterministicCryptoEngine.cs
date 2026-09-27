using System.Security.Cryptography;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.Ports;
using Percolator.Domain.Security.ValueObjects;

namespace Percolator.Domain.Tests.TestDoubles;

public sealed class DeterministicCryptoEngine : ICryptoEngine
{
    public bool SignaturesAlwaysValid { get; set; } = true;
    private byte _keyPairCounter = 1;

    public (ChainKey NextChainKey, MessageKey DerivedMessageKey) StepRatchet(ChainKey currentChainKey)
    {
        Span<byte> nextChainBytes = stackalloc byte[32];
        Span<byte> messageKeyBytes = stackalloc byte[32];

        Span<byte> buffer = stackalloc byte[33];
        currentChainKey.Span.CopyTo(buffer);

        buffer[32] = 0x01;
        SHA256.HashData(buffer, nextChainBytes);

        buffer[32] = 0x02;
        SHA256.HashData(buffer, messageKeyBytes);

        return (ChainKey.FromSpan(nextChainBytes), MessageKey.FromSpan(messageKeyBytes));
    }

    public (ChainKey NextRootKey, ChainKey DerivedChainKey) KdfRk(ChainKey currentRootKey, SharedSecret dhSecret)
    {
        Span<byte> nextRootBytes = stackalloc byte[32];
        Span<byte> chainKeyBytes = stackalloc byte[32];

        Span<byte> buffer = stackalloc byte[65];
        currentRootKey.Span.CopyTo(buffer);
        dhSecret.Span.CopyTo(buffer[32..64]);

        buffer[64] = 0x10;
        SHA256.HashData(buffer, nextRootBytes);

        buffer[64] = 0x20;
        SHA256.HashData(buffer, chainKeyBytes);

        return (ChainKey.FromSpan(nextRootBytes), ChainKey.FromSpan(chainKeyBytes));
    }

    public (EphemeralPrivateKey PrivateKey, IdentityPublicKey PublicKey) GenerateEphemeralKeyPair()
    {
        byte val = _keyPairCounter++;
        Span<byte> priv = stackalloc byte[32];
        Span<byte> pub = stackalloc byte[32];
        priv.Fill(val);
        pub.Fill((byte)(val + 100));
        return (EphemeralPrivateKey.FromSpan(priv), IdentityPublicKey.FromSpan(pub));
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

    public bool VerifyEd25519Signature(IdentityPublicKey publicKey, ReadOnlySpan<byte> message, ReadOnlySpan<byte> signature)
    {
        return SignaturesAlwaysValid;
    }
}
