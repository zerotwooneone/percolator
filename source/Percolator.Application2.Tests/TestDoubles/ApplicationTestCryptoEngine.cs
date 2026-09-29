using System.Security.Cryptography;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.Ports;
using Percolator.Domain.Security.ValueObjects;

namespace Percolator.Application2.Tests.TestDoubles;

public sealed class ApplicationTestCryptoEngine : ICryptoEngine
{
    private byte _keyPairCounter = 1;
    public bool ShouldFailDecryption { get; set; } = false;

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
        byte privVal = privateKey.Length > 0 ? privateKey[0] : (byte)0;
        byte pubVal = publicKey.Length > 0 ? (publicKey[0] >= 100 ? (byte)(publicKey[0] - 100) : publicKey[0]) : (byte)0;
        byte valA = Math.Min(privVal, pubVal);
        byte valB = Math.Max(privVal, pubVal);
        Span<byte> combined = stackalloc byte[2];
        combined[0] = valA;
        combined[1] = valB;
        SHA256.HashData(combined, hash);
        return SharedSecret.FromSpan(hash);
    }

    public SharedSecret DeriveX3dhMasterSecret(
        ReadOnlySpan<byte> dh1,
        ReadOnlySpan<byte> dh2,
        ReadOnlySpan<byte> dh3,
        ReadOnlySpan<byte> dh4 = default)
    {
        int totalLength = dh1.Length + dh2.Length + dh3.Length + (dh4.IsEmpty ? 0 : dh4.Length);
        Span<byte> buffer = stackalloc byte[totalLength];
        dh1.CopyTo(buffer);
        dh2.CopyTo(buffer[dh1.Length..]);
        dh3.CopyTo(buffer[(dh1.Length + dh2.Length)..]);
        if (!dh4.IsEmpty)
        {
            dh4.CopyTo(buffer[(dh1.Length + dh2.Length + dh3.Length)..]);
        }

        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(buffer, hash);
        return SharedSecret.FromSpan(hash);
    }

    public byte[] EncryptAesGcm(ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> associatedData)
    {
        // Simple mock encryption: tag (16 bytes) + plaintext
        var result = new byte[16 + plaintext.Length];
        associatedData[..Math.Min(16, associatedData.Length)].CopyTo(result.AsSpan(0, 16));
        plaintext.CopyTo(result.AsSpan(16));
        return result;
    }

    public byte[] DecryptAesGcm(ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> associatedData)
    {
        if (ShouldFailDecryption)
        {
            throw new CryptographicException("Authentication tag mismatch.");
        }

        if (ciphertext.Length < 16)
        {
            throw new CryptographicException("Ciphertext too short.");
        }

        // Return the plaintext payload after the 16-byte tag
        return ciphertext[16..].ToArray();
    }

    public bool VerifyEd25519Signature(IdentityKey publicKey, ReadOnlySpan<byte> message, ReadOnlySpan<byte> signature)
    {
        return true;
    }
}
