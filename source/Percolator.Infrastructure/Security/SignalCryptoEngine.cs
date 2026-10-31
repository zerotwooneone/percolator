namespace Percolator.Infrastructure.Security;

using System;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using Percolator.Domain.Common;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.Ports;
using Percolator.Domain.Security.ValueObjects;
using Signal.Interop;

public sealed class CryptographyOptions
{
    public bool EnableCryptographicMaterialLogging { get; set; } = false;
}

public sealed class SignalCryptoEngine : ICryptoEngine
{
    private const int TagSize = 16;
    private readonly CryptographyOptions _options;

    public SignalCryptoEngine(CryptographyOptions? options = null)
    {
        _options = options ?? new CryptographyOptions();
    }

    public (ChainKey NextChainKey, MessageKey DerivedMessageKey) StepRatchet(ChainKey currentChainKey)
    {
        Span<byte> derived = stackalloc byte[64];
        HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            currentChainKey.Span,
            derived,
            salt: default,
            info: "dr-symmetric-ratchet-step"u8);

        var nextChainKey = ChainKey.FromSpan(derived[..32]);
        var messageKey = MessageKey.FromSpan(derived[32..]);
        CryptographicOperations.ZeroMemory(derived);

        return (nextChainKey, messageKey);
    }

    public (ChainKey NextRootKey, ChainKey DerivedChainKey) KdfRk(ChainKey currentRootKey, SharedSecret dhSecret)
    {
        Span<byte> derived = stackalloc byte[64];
        HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            dhSecret.Span,
            derived,
            salt: currentRootKey.Span,
            info: "dr-dh-ratchet-kdf-rk"u8);

        var nextRootKey = ChainKey.FromSpan(derived[..32]);
        var derivedChainKey = ChainKey.FromSpan(derived[32..]);
        CryptographicOperations.ZeroMemory(derived);

        return (nextRootKey, derivedChainKey);
    }

    public SharedSecret ComputeDiffieHellman(ReadOnlySpan<byte> privateKey, ReadOnlySpan<byte> publicKey)
    {
        if (privateKey.Length != 32 || publicKey.Length != 32)
        {
            throw new ArgumentException("Keys must be 32 bytes for X25519 Diffie-Hellman.");
        }

        Span<byte> secret = stackalloc byte[32];
        Curve25519Core.X25519(secret, privateKey, publicKey);
        var result = SharedSecret.FromSpan(secret);
        CryptographicOperations.ZeroMemory(secret);
        return result;
    }

    public (EphemeralPrivateKey PrivateKey, DhPublicKey PublicKey) GenerateEphemeralKeyPair()
    {
        Span<byte> privBytes = stackalloc byte[32];
        Span<byte> pubBytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(privBytes);

        Curve25519Core.X25519BasePoint(pubBytes, privBytes);

        var priv = EphemeralPrivateKey.FromSpan(privBytes);
        var pub = DhPublicKey.FromSpan(pubBytes);
        CryptographicOperations.ZeroMemory(privBytes);
        return (priv, pub);
    }

    public SharedSecret DeriveX3dhMasterSecret(
        ReadOnlySpan<byte> dh1,
        ReadOnlySpan<byte> dh2,
        ReadOnlySpan<byte> dh3,
        ReadOnlySpan<byte> dh4 = default)
    {
        int totalLen = dh1.Length + dh2.Length + dh3.Length + (dh4.IsEmpty ? 0 : dh4.Length);
        Span<byte> concat = stackalloc byte[totalLen];
        dh1.CopyTo(concat);
        dh2.CopyTo(concat[dh1.Length..]);
        dh3.CopyTo(concat[(dh1.Length + dh2.Length)..]);
        if (!dh4.IsEmpty)
        {
            dh4.CopyTo(concat[(dh1.Length + dh2.Length + dh3.Length)..]);
        }

        Span<byte> master = stackalloc byte[32];
        HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            concat,
            master,
            salt: default,
            info: "x3dh-master-shared-secret"u8);

        CryptographicOperations.ZeroMemory(concat);
        var result = SharedSecret.FromSpan(master);
        CryptographicOperations.ZeroMemory(master);
        return result;
    }

    public byte[] EncryptAesGcm(ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> associatedData)
    {
        using var aes = new AesGcm(key, TagSize);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSize];

        aes.Encrypt(nonce, plaintext, ciphertext, tag, associatedData);

        var result = new byte[ciphertext.Length + tag.Length];
        Buffer.BlockCopy(ciphertext, 0, result, 0, ciphertext.Length);
        Buffer.BlockCopy(tag, 0, result, ciphertext.Length, tag.Length);
        return result;
    }

    public byte[] DecryptAesGcm(ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> associatedData)
    {
        if (ciphertext.Length < TagSize)
        {
            throw new CryptographicException("Ciphertext shorter than authentication tag size.");
        }

        using var aes = new AesGcm(key, TagSize);
        int ctLen = ciphertext.Length - TagSize;
        var ctSpan = ciphertext[..ctLen];
        var tagSpan = ciphertext[ctLen..];

        var plaintext = new byte[ctLen];
        aes.Decrypt(nonce, ctSpan, tagSpan, plaintext, associatedData);
        return plaintext;
    }

    public bool VerifyEd25519Signature(IdentityKey publicKey, ReadOnlySpan<byte> message, ReadOnlySpan<byte> signature)
    {
        if (signature.Length != 64 || publicKey.Length != 32)
        {
            return false;
        }

        try
        {
            return SignalCrypto.Ed25519Verify(publicKey.Span, message, signature);
        }
        catch
        {
            return false;
        }
    }

    public byte[] SignEd25519(ReadOnlySpan<byte> privateKey, ReadOnlySpan<byte> message)
    {
        if (privateKey.Length != 32)
        {
            throw new ArgumentException("Ed25519 private key seed must be 32 bytes.");
        }

        byte[] sig = new byte[64];
        SignalCrypto.Ed25519Sign(privateKey, message, sig);
        return sig;
    }

    public byte[] HkdfSha256(ReadOnlySpan<byte> ikm, ReadOnlySpan<byte> salt, ReadOnlySpan<byte> info, int outputLength)
    {
        byte[] output = new byte[outputLength];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, output, salt, info);
        return output;
    }

    internal static class Curve25519Core
    {
        private static readonly BigInteger P = BigInteger.Pow(2, 255) - 19;
        private static readonly BigInteger A24 = 121665;

        private static BigInteger Mod(BigInteger a, BigInteger m)
        {
            BigInteger r = a % m;
            return r < 0 ? r + m : r;
        }

        public static void X25519BasePoint(Span<byte> result, ReadOnlySpan<byte> scalar)
        {
            Span<byte> basePoint = stackalloc byte[32];
            basePoint[0] = 9;
            X25519(result, scalar, basePoint);
        }

        public static void X25519(Span<byte> result, ReadOnlySpan<byte> scalar, ReadOnlySpan<byte> uCoordinate)
        {
            Span<byte> clamped = stackalloc byte[32];
            scalar.CopyTo(clamped);
            clamped[0] &= 248;
            clamped[31] &= 127;
            clamped[31] |= 64;

            Span<byte> uBytes = stackalloc byte[32];
            uCoordinate.CopyTo(uBytes);
            uBytes[31] &= 127;

            BigInteger k = ToUnsignedBigInteger(clamped);
            BigInteger u = Mod(ToUnsignedBigInteger(uBytes), P);

            BigInteger x1 = u;
            BigInteger x2 = 1;
            BigInteger z2 = 0;
            BigInteger x3 = u;
            BigInteger z3 = 1;
            int swap = 0;

            for (int t = 254; t >= 0; t--)
            {
                int kt = (int)((k >> t) & 1);
                swap ^= kt;
                if (swap == 1)
                {
                    (x2, x3) = (x3, x2);
                    (z2, z3) = (z3, z2);
                }
                swap = kt;

                BigInteger a = Mod(x2 + z2, P);
                BigInteger aa = Mod(a * a, P);
                BigInteger b = Mod(x2 - z2, P);
                BigInteger bb = Mod(b * b, P);
                BigInteger e = Mod(aa - bb, P);
                BigInteger c = Mod(x3 + z3, P);
                BigInteger d = Mod(x3 - z3, P);
                BigInteger da = Mod(d * a, P);
                BigInteger cb = Mod(c * b, P);

                x3 = BigInteger.ModPow(Mod(da + cb, P), 2, P);
                z3 = Mod(x1 * BigInteger.ModPow(Mod(da - cb, P), 2, P), P);
                x2 = Mod(aa * bb, P);
                z2 = Mod(e * Mod(aa + A24 * e, P), P);
            }

            if (swap == 1)
            {
                (x2, x3) = (x3, x2);
                (z2, z3) = (z3, z2);
            }

            BigInteger invZ2 = BigInteger.ModPow(z2, P - 2, P);
            BigInteger res = Mod(x2 * invZ2, P);

            To32BytesLittleEndian(res, result);
            CryptographicOperations.ZeroMemory(clamped);
        }

        internal static BigInteger ToUnsignedBigInteger(ReadOnlySpan<byte> span)
        {
            byte[] buf = new byte[span.Length + 1];
            span.CopyTo(buf);
            return new BigInteger(buf);
        }

        internal static void To32BytesLittleEndian(BigInteger value, Span<byte> destination)
        {
            destination.Clear();
            byte[] bytes = value.ToByteArray();
            int copyLen = Math.Min(32, bytes.Length);
            bytes.AsSpan(0, copyLen).CopyTo(destination);
        }
    }
}
