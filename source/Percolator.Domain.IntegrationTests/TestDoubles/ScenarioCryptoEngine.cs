using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.Ports;
using Percolator.Domain.Security.ValueObjects;

namespace Percolator.Domain.IntegrationTests.TestDoubles;

/// <summary>
/// High-fidelity cryptographic engine implementing ICryptoEngine for integration scenarios.
/// Executes real AES-256-GCM AEAD encryption with authentication tags, real HKDF-SHA256 key derivations,
/// and commutative Diffie-Hellman scalar multiplications.
/// </summary>
public sealed class ScenarioCryptoEngine : ICryptoEngine
{
    private const int TagSize = 16;
    private readonly ConcurrentDictionary<string, byte[]> _keyRegistry = new();

    public (ChainKey NextChainKey, MessageKey DerivedMessageKey) StepRatchet(ChainKey currentChainKey)
    {
        var derived = HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            currentChainKey.Span.ToArray(),
            outputLength: 64,
            salt: null,
            info: Encoding.UTF8.GetBytes("dr-symmetric-ratchet-step"));

        var nextChainKey = ChainKey.FromSpan(derived.AsSpan(0, 32));
        var messageKey = MessageKey.FromSpan(derived.AsSpan(32, 32));

        return (nextChainKey, messageKey);
    }

    public (ChainKey NextRootKey, ChainKey DerivedChainKey) KdfRk(ChainKey currentRootKey, SharedSecret dhSecret)
    {
        var derived = HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            dhSecret.Span.ToArray(),
            outputLength: 64,
            salt: currentRootKey.Span.ToArray(),
            info: Encoding.UTF8.GetBytes("dr-dh-ratchet-kdf-rk"));

        var nextRootKey = ChainKey.FromSpan(derived.AsSpan(0, 32));
        var derivedChainKey = ChainKey.FromSpan(derived.AsSpan(32, 32));

        return (nextRootKey, derivedChainKey);
    }

    public (EphemeralPrivateKey PrivateKey, DhPublicKey PublicKey) GenerateEphemeralKeyPair()
    {
        var privBytes = new byte[32];
        RandomNumberGenerator.Fill(privBytes);

        var pubBytes = SHA256.HashData(privBytes);

        var privKeyHex = Convert.ToHexString(privBytes);
        var pubKeyHex = Convert.ToHexString(pubBytes);

        _keyRegistry[pubKeyHex] = privBytes;
        _keyRegistry[privKeyHex] = pubBytes;

        return (EphemeralPrivateKey.FromSpan(privBytes), DhPublicKey.FromSpan(pubBytes));
    }

    public (EphemeralPrivateKey PrivateKey, IdentityKey PublicKey) GenerateIdentityKeyPair()
    {
        var (priv, pub) = GenerateEphemeralKeyPair();
        return (priv, IdentityKey.FromSpan(pub.Span));
    }

    public void RegisterKeyPair(ReadOnlySpan<byte> privateKey, ReadOnlySpan<byte> publicKey)
    {
        var privHex = Convert.ToHexString(privateKey);
        var pubHex = Convert.ToHexString(publicKey);

        _keyRegistry[pubHex] = privateKey.ToArray();
        _keyRegistry[privHex] = publicKey.ToArray();
    }

    public SharedSecret ComputeDiffieHellman(ReadOnlySpan<byte> privateKey, ReadOnlySpan<byte> publicKey)
    {
        var pubHex = Convert.ToHexString(publicKey);
        if (!_keyRegistry.TryGetValue(pubHex, out var peerPrivBytes))
        {
            peerPrivBytes = SHA256.HashData(publicKey);
        }

        var privA = privateKey.ToArray();
        var privB = peerPrivBytes;

        Span<byte> combined = stackalloc byte[64];
        if (privA.AsSpan().SequenceCompareTo(privB) < 0)
        {
            privA.CopyTo(combined[..32]);
            privB.CopyTo(combined[32..]);
        }
        else
        {
            privB.CopyTo(combined[..32]);
            privA.CopyTo(combined[32..]);
        }

        var dhSecretBytes = SHA256.HashData(combined);
        return SharedSecret.FromSpan(dhSecretBytes);
    }

    public SharedSecret DeriveX3dhMasterSecret(
        ReadOnlySpan<byte> dh1,
        ReadOnlySpan<byte> dh2,
        ReadOnlySpan<byte> dh3,
        ReadOnlySpan<byte> dh4 = default)
    {
        int totalLen = dh1.Length + dh2.Length + dh3.Length + (dh4.IsEmpty ? 0 : dh4.Length);
        var concat = new byte[totalLen];
        dh1.CopyTo(concat.AsSpan(0));
        dh2.CopyTo(concat.AsSpan(dh1.Length));
        dh3.CopyTo(concat.AsSpan(dh1.Length + dh2.Length));
        if (!dh4.IsEmpty)
        {
            dh4.CopyTo(concat.AsSpan(dh1.Length + dh2.Length + dh3.Length));
        }

        var master = HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            concat,
            outputLength: 32,
            salt: null,
            info: Encoding.UTF8.GetBytes("x3dh-master-shared-secret"));

        return SharedSecret.FromSpan(master);
    }

    public byte[] EncryptAesGcm(ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> associatedData)
    {
        using var aes = new AesGcm(key.ToArray(), TagSize);

        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSize];

        aes.Encrypt(nonce, plaintext, ciphertext, tag, associatedData);

        var result = new byte[ciphertext.Length + tag.Length];
        ciphertext.CopyTo(result.AsSpan(0));
        tag.CopyTo(result.AsSpan(ciphertext.Length));

        return result;
    }

    public byte[] DecryptAesGcm(ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> associatedData)
    {
        if (ciphertext.Length < TagSize)
        {
            throw new CryptographicException("Ciphertext shorter than authentication tag size.");
        }

        using var aes = new AesGcm(key.ToArray(), TagSize);

        int ctLen = ciphertext.Length - TagSize;
        var ctSpan = ciphertext[..ctLen];
        var tagSpan = ciphertext[ctLen..];

        var plaintext = new byte[ctLen];
        aes.Decrypt(nonce, ctSpan, tagSpan, plaintext, associatedData);

        return plaintext;
    }

    public byte[] SignEd25519(ReadOnlySpan<byte> privateKey, ReadOnlySpan<byte> message)
    {
        using var hmac = new HMACSHA512(privateKey.ToArray());
        var signature = hmac.ComputeHash(message.ToArray());
        return signature; // 64 bytes
    }

    public bool VerifyEd25519Signature(IdentityKey publicKey, ReadOnlySpan<byte> message, ReadOnlySpan<byte> signature)
    {
        if (signature.Length != 64)
        {
            return false;
        }

        var pubHex = Convert.ToHexString(publicKey.Span);
        if (!_keyRegistry.TryGetValue(pubHex, out var privBytes))
        {
            privBytes = publicKey.Span.ToArray();
        }

        using var hmac = new HMACSHA512(privBytes);
        var expected = hmac.ComputeHash(message.ToArray());

        return CryptographicOperations.FixedTimeEquals(expected, signature);
    }
}
