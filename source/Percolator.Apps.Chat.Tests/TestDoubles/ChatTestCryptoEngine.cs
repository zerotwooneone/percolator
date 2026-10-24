using System.Security.Cryptography;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.Ports;
using Percolator.Domain.Security.ValueObjects;

namespace Percolator.Apps.Chat.Tests.TestDoubles;

public sealed class ChatTestCryptoEngine : ICryptoEngine
{
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
        dhSecret.Span.CopyTo(buffer[32..]);
        buffer[64] = 0x10;
        SHA256.HashData(buffer, nextRootBytes);
        buffer[64] = 0x20;
        SHA256.HashData(buffer, chainKeyBytes);
        return (ChainKey.FromSpan(nextRootBytes), ChainKey.FromSpan(chainKeyBytes));
    }

    public (EphemeralPrivateKey PrivateKey, DhPublicKey PublicKey) GenerateEphemeralKeyPair()
    {
        byte val = _keyPairCounter++;
        Span<byte> priv = stackalloc byte[32];
        Span<byte> pub = stackalloc byte[32];
        priv.Fill(val);
        pub.Fill((byte)(val + 100));
        return (EphemeralPrivateKey.FromSpan(priv), DhPublicKey.FromSpan(pub));
    }

    public SharedSecret ComputeDiffieHellman(ReadOnlySpan<byte> privateKey, ReadOnlySpan<byte> publicKey)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(privateKey, hash);
        return SharedSecret.FromSpan(hash);
    }

    public SharedSecret DeriveX3dhMasterSecret(
        ReadOnlySpan<byte> dh1,
        ReadOnlySpan<byte> dh2,
        ReadOnlySpan<byte> dh3,
        ReadOnlySpan<byte> dh4 = default)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(dh1, hash);
        return SharedSecret.FromSpan(hash);
    }

    public byte[] EncryptAesGcm(ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> associatedData)
    {
        var result = new byte[16 + plaintext.Length];
        plaintext.CopyTo(result.AsSpan(16));
        return result;
    }

    public byte[] DecryptAesGcm(ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> associatedData)
    {
        if (ciphertext.Length < 16) return [];
        return ciphertext[16..].ToArray();
    }

    public bool VerifyEd25519Signature(IdentityKey publicKey, ReadOnlySpan<byte> message, ReadOnlySpan<byte> signature)
    {
        return true;
    }

    public byte[] SignEd25519(ReadOnlySpan<byte> privateKey, ReadOnlySpan<byte> message)
    {
        return new byte[64];
    }

    public byte[] HkdfSha256(ReadOnlySpan<byte> ikm, ReadOnlySpan<byte> salt, ReadOnlySpan<byte> info, int outputLength)
    {
        var output = new byte[outputLength];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, output, salt, info);
        return output;
    }
}
