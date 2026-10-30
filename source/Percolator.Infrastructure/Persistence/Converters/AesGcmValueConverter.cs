namespace Percolator.Infrastructure.Persistence.Converters;

using System;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Percolator.Domain.Security.Ports;

public sealed class AesGcmValueConverter : ValueConverter<byte[], byte[]>
{
    public AesGcmValueConverter(IMasterKeyHandle masterKeyHandle)
        : base(
            plainBytes => EncryptBytes(plainBytes, masterKeyHandle),
            cipherBytes => DecryptBytes(cipherBytes, masterKeyHandle))
    {
    }

    internal static byte[] EncryptBytes(byte[]? plaintext, IMasterKeyHandle masterKeyHandle)
    {
        if (plaintext == null || plaintext.Length == 0) return Array.Empty<byte>();

        return masterKeyHandle.WithKeySpan(keySpan =>
        {
            Span<byte> colKey = stackalloc byte[32];
            HKDF.DeriveKey(HashAlgorithmName.SHA256, keySpan, colKey, salt: default, info: "Percolator.ColumnLevelEncryption.v1"u8);
            try
            {
                using var aes = new AesGcm(colKey, AesGcm.TagByteSizes.MaxSize);
                byte[] nonce = RandomNumberGenerator.GetBytes(AesGcm.NonceByteSizes.MaxSize); // 12 bytes
                byte[] tag = new byte[AesGcm.TagByteSizes.MaxSize]; // 16 bytes
                byte[] ciphertext = new byte[plaintext.Length];

                aes.Encrypt(nonce, plaintext, ciphertext, tag);

                // Payload layout: [12-byte Nonce] [16-byte Tag] [N-byte Ciphertext]
                byte[] result = new byte[nonce.Length + tag.Length + ciphertext.Length];
                Buffer.BlockCopy(nonce, 0, result, 0, nonce.Length);
                Buffer.BlockCopy(tag, 0, result, nonce.Length, tag.Length);
                Buffer.BlockCopy(ciphertext, 0, result, nonce.Length + tag.Length, ciphertext.Length);
                return result;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(colKey);
            }
        });
    }

    internal static byte[] DecryptBytes(byte[]? payload, IMasterKeyHandle masterKeyHandle)
    {
        if (payload == null || payload.Length < 28) return Array.Empty<byte>();

        return masterKeyHandle.WithKeySpan(keySpan =>
        {
            Span<byte> colKey = stackalloc byte[32];
            HKDF.DeriveKey(HashAlgorithmName.SHA256, keySpan, colKey, salt: default, info: "Percolator.ColumnLevelEncryption.v1"u8);
            try
            {
                using var aes = new AesGcm(colKey, AesGcm.TagByteSizes.MaxSize);
                ReadOnlySpan<byte> nonce = payload.AsSpan(0, 12);
                ReadOnlySpan<byte> tag = payload.AsSpan(12, 16);
                ReadOnlySpan<byte> ciphertext = payload.AsSpan(28);

                byte[] plaintext = new byte[ciphertext.Length];
                aes.Decrypt(nonce, ciphertext, tag, plaintext);
                return plaintext;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(colKey);
            }
        });
    }
}

public sealed class NullableAesGcmValueConverter : ValueConverter<byte[]?, byte[]?>
{
    public NullableAesGcmValueConverter(IMasterKeyHandle masterKeyHandle)
        : base(
            plainBytes => plainBytes == null ? null : AesGcmValueConverter.EncryptBytes(plainBytes, masterKeyHandle),
            cipherBytes => cipherBytes == null ? null : AesGcmValueConverter.DecryptBytes(cipherBytes, masterKeyHandle))
    {
    }
}
