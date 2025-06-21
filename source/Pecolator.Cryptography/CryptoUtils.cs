using System;
using System.Security.Cryptography;

namespace Pecolator.Cryptography
{
    internal static class CryptoUtils
    {
        internal const int KeySize = 32; // 256 bits

        internal static (byte[] out1, byte[] out2) KDF(byte[] key)
        {
            using var hmac = new HMACSHA256(key);
            var buffer = new byte[KeySize];

            hmac.TryComputeHash(new byte[] { 1 }, buffer, out _);
            var out1 = buffer.ToArray();

            hmac.TryComputeHash(new byte[] { 2 }, buffer, out _);
            var out2 = buffer.ToArray();

            return (out1, out2);
        }

        internal static byte[] EncryptAesGcm(byte[] key, uint counter, byte[] plaintext, byte[]? associatedData)
        {
            var nonce = new byte[12];
            BitConverter.GetBytes(counter).CopyTo(nonce, 0);

            using var aes = new AesGcm(key, 16);
            var ciphertext = new byte[plaintext.Length];
            var tag = new byte[16];
            aes.Encrypt(nonce, plaintext, ciphertext, tag, associatedData);

            var result = new byte[ciphertext.Length + tag.Length];
            ciphertext.CopyTo(result, 0);
            tag.CopyTo(result, ciphertext.Length);
            return result;
        }

        internal static byte[] DecryptAesGcm(byte[] key, uint counter, byte[] ciphertextWithTag, byte[]? associatedData)
        {
            var nonce = new byte[12];
            BitConverter.GetBytes(counter).CopyTo(nonce, 0);

            using var aes = new AesGcm(key, 16);
            var tag = new byte[16];
            var ciphertext = new byte[ciphertextWithTag.Length - 16];

            Array.Copy(ciphertextWithTag, ciphertextWithTag.Length - 16, tag, 0, 16);
            Array.Copy(ciphertextWithTag, 0, ciphertext, 0, ciphertextWithTag.Length - 16);

            var plaintext = new byte[ciphertext.Length];
            aes.Decrypt(nonce, ciphertext, tag, plaintext, associatedData);

            return plaintext;
        }
    }
}
