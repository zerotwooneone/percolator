using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

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

        /// <summary>
        /// Encrypts data using AES-GCM with a deterministic, counter-based nonce.
        /// </summary>
        /// <remarks>
        /// <strong>Security Warning:</strong> This method uses a deterministic nonce generated from the <paramref name="counter"/>.
        /// It is critically important that the same combination of <paramref name="key"/> and <paramref name="counter"/>
        /// is NEVER used to encrypt different plaintext data. Reusing a key/nonce pair completely destroys the confidentiality
        /// of the messages encrypted with it. This method is safe only when the key is ratcheted forward with every encryption,
        /// as is done in the Double Ratchet and Sender Key protocols. For general-purpose encryption, use a method that
        /// generates a random nonce for each operation.
        /// </remarks>
        /// <param name="key">The 32-byte encryption key.</param>
        /// <param name="counter">The unique counter for this encryption operation. This will be used to generate the nonce.</param>
        /// <param name="plaintext">The data to encrypt.</param>
        /// <param name="associatedData">Optional associated data to be authenticated but not encrypted.</param>
        /// <returns>The ciphertext concatenated with the authentication tag.</returns>
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

        /// <summary>
        /// Encrypts data using AES-GCM with a random nonce.
        /// </summary>
        /// <param name="plaintext">The data to encrypt.</param>
        /// <param name="key">The 32-byte encryption key.</param>
        /// <param name="associatedData">Optional associated data to be authenticated but not encrypted.</param>
        /// <returns>The ciphertext concatenated with the authentication tag.</returns>
        public static byte[] EncryptAesGcm(byte[] plaintext, byte[] key, byte[]? associatedData = null)
        {
            var nonce = RandomNumberGenerator.GetBytes(12);
            
            using var aes = new AesGcm(key, TagSize);
            var ciphertext = new byte[plaintext.Length];
            var tag = new byte[TagSize];
            aes.Encrypt(nonce, plaintext, ciphertext, tag, associatedData);

            var result = new byte[nonce.Length + ciphertext.Length + tag.Length];
            nonce.CopyTo(result, 0);
            ciphertext.CopyTo(result, nonce.Length);
            tag.CopyTo(result, nonce.Length + ciphertext.Length);
            return result;
        }

        /// <summary>
        /// Decrypts data encrypted with the random-nonce AES-GCM method.
        /// </summary>
        /// <param name="ciphertextWithNonceAndTag">The ciphertext concatenated with nonce and authentication tag.</param>
        /// <param name="key">The 32-byte encryption key.</param>
        /// <param name="associatedData">Optional associated data that was authenticated during encryption.</param>
        /// <returns>The decrypted plaintext.</returns>
        public static byte[] DecryptAesGcm(byte[] ciphertextWithNonceAndTag, byte[] key, byte[]? associatedData = null)
        {
            var nonce = ciphertextWithNonceAndTag[..12];
            var tagOffset = ciphertextWithNonceAndTag.Length - TagSize;
            var ciphertext = ciphertextWithNonceAndTag[12..tagOffset];
            var tag = ciphertextWithNonceAndTag[tagOffset..];

            using var aes = new AesGcm(key, TagSize);
            var plaintext = new byte[ciphertext.Length];
            aes.Decrypt(nonce, ciphertext, tag, plaintext, associatedData);
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

        public static (ECDsa privateKey, ECDsa publicKey) GenerateNewKeys()
        {
            var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            return (key, ECDsa.Create(key.ExportParameters(false)));
        }

        public static byte[] Sign(byte[] data, ECDsa privateKey)
        {
            return privateKey.SignData(data, HashAlgorithmName.SHA256);
        }

        public static bool Verify(byte[] data, byte[] signature, ECDsa publicKey)
        {
            return publicKey.VerifyData(data, signature, HashAlgorithmName.SHA256);
        }
    }
}