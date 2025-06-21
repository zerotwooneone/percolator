using System;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;
using Percolator.Cryptography.Protos;

namespace Percolator.Cryptography
{
    public static class CryptoUtils
    {
        public const int KeySize = 32;
        public const int TagSize = 16;

        public static byte[] KDF(byte[]? salt, byte[] key, string info, int outputLength)
        {
            return HKDF.DeriveKey(
                HashAlgorithmName.SHA256,
                key,
                outputLength,
                salt,
                Encoding.UTF8.GetBytes(info));
        }

        public static byte[] EncryptAesGcm(byte[] key, ulong counter, byte[] plaintext, byte[]? associatedData)
        {
            var nonceBytes = new byte[12];
            BinaryPrimitives.WriteUInt64BigEndian(nonceBytes, counter);

            using var aes = new AesGcm(key, TagSize);
            var ciphertext = new byte[plaintext.Length];
            var tag = new byte[TagSize];
            aes.Encrypt(nonceBytes, plaintext, ciphertext, tag, associatedData);

            var result = new byte[ciphertext.Length + tag.Length];
            ciphertext.CopyTo(result, 0);
            tag.CopyTo(result, ciphertext.Length);
            return result;
        }

        public static byte[] DecryptAesGcm(byte[] key, ulong counter, byte[] ciphertextWithTag, byte[]? associatedData)
        {
            var nonceBytes = new byte[12];
            BinaryPrimitives.WriteUInt64BigEndian(nonceBytes, counter);

            using var aes = new AesGcm(key, TagSize);
            var tagOffset = ciphertextWithTag.Length - TagSize;
            var ciphertext = ciphertextWithTag[..tagOffset];
            var tag = ciphertextWithTag[tagOffset..];

            var plaintext = new byte[ciphertext.Length];
            aes.Decrypt(nonceBytes, ciphertext, tag, plaintext, associatedData);
            return plaintext;
        }

        public static string ToHex(byte[] data)
        {
            return Convert.ToHexString(data);
        }

        public static byte[] EncryptAtRest(byte[] masterKey, byte[] data, byte[]? associatedData)
        {
            var salt = RandomNumberGenerator.GetBytes(16);
            var key = KDF(salt, masterKey, "encrypt-at-rest", KeySize);

            using var aes = new AesGcm(key, TagSize);
            var nonce = RandomNumberGenerator.GetBytes(12);
            var ciphertext = new byte[data.Length];
            var tag = new byte[TagSize];
            aes.Encrypt(nonce, data, ciphertext, tag, associatedData);

            var result = new byte[salt.Length + nonce.Length + tag.Length + ciphertext.Length];
            salt.CopyTo(result, 0);
            nonce.CopyTo(result, salt.Length);
            tag.CopyTo(result, salt.Length + nonce.Length);
            ciphertext.CopyTo(result, salt.Length + nonce.Length + tag.Length);
            return result;
        }

        public static byte[] DecryptAtRest(byte[] masterKey, byte[] encryptedPayload, byte[]? associatedData)
        {
            var salt = encryptedPayload[..16];
            var nonce = encryptedPayload[16..28];
            var tag = encryptedPayload[28..44];
            var ciphertext = encryptedPayload[44..];

            var key = KDF(salt, masterKey, "encrypt-at-rest", KeySize);

            using var aes = new AesGcm(key, TagSize);
            var plaintext = new byte[ciphertext.Length];
            aes.Decrypt(nonce, ciphertext, tag, plaintext, associatedData);
            return plaintext;
        }

        private static byte[] GetCanonicalBytes(Manifest manifest)
        {
            var canonicalManifest = manifest.Clone();
            // Sort the entries by path to ensure a deterministic byte representation for signing
            var sortedEntries = canonicalManifest.Entries.OrderBy(e => e.Path, StringComparer.Ordinal).ToList();
            canonicalManifest.Entries.Clear();
            canonicalManifest.Entries.AddRange(sortedEntries);
            return canonicalManifest.ToByteArray();
        }

        public static ByteString SignManifest(Manifest manifest, ECDsa privateKey)
        {
            var canonicalBytes = GetCanonicalBytes(manifest);
            var signature = privateKey.SignData(canonicalBytes, HashAlgorithmName.SHA256);
            return ByteString.CopyFrom(signature);
        }

        public static bool VerifyManifest(SignedManifest signedManifest, TimeSpan? maxAge = null)
        {
            if (signedManifest.Manifest == null || !signedManifest.HasSignature || !signedManifest.Manifest.HasAuthorIdentityPublicKey)
            {
                return false;
            }

            if (maxAge.HasValue) 
            {
                if (signedManifest.Manifest.TimestampUtc == null) return false;
                var timestamp = signedManifest.Manifest.TimestampUtc.ToDateTime();
                if (timestamp < DateTime.UtcNow - maxAge.Value) 
                {
                    return false; // Timestamp is too old
                }
            }

            using var verifier = ECDsa.Create();
            verifier.ImportSubjectPublicKeyInfo(signedManifest.Manifest.AuthorIdentityPublicKey.ToByteArray(), out _);

            var canonicalBytes = GetCanonicalBytes(signedManifest.Manifest);
            return verifier.VerifyData(canonicalBytes, signedManifest.Signature.ToByteArray(), HashAlgorithmName.SHA256);
        }
    }
}