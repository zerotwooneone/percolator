using System.Buffers.Binary;
using System.Security.Cryptography;
using Percolator.Apps.FileTransfer.Domain.Common;
using Percolator.Apps.FileTransfer.Features.TransferNegotiation;

namespace Percolator.Apps.FileTransfer.Features.MultiStreamEngine;

public sealed record ChunkNonce
{
    private readonly byte[] _bytes;
    public ReadOnlySpan<byte> Span => _bytes;
    public byte[] Bytes => (byte[])_bytes.Clone();

    private ChunkNonce(byte[] bytes) => _bytes = bytes;

    public static ChunkNonce FromBytes(byte[] bytes)
    {
        if (bytes == null || bytes.Length != 12)
            throw new ArgumentException("ChunkNonce must be 12 bytes.", nameof(bytes));
        return new ChunkNonce((byte[])bytes.Clone());
    }

    public static ChunkNonce Derive(BaseNonce baseNonce, ChunkIndex chunkIndex)
    {
        byte[] derived = new byte[12];
        DeriveInto(baseNonce, chunkIndex, derived);
        return new ChunkNonce(derived);
    }

    public static void DeriveInto(BaseNonce baseNonce, ChunkIndex chunkIndex, Span<byte> destination)
    {
        if (destination.Length < 12)
            throw new ArgumentException("Destination span must be at least 12 bytes.", nameof(destination));

        baseNonce.Span.CopyTo(destination[..12]);
        int currentBigEndian = BinaryPrimitives.ReadInt32BigEndian(destination.Slice(8, 4));
        BinaryPrimitives.WriteInt32BigEndian(destination.Slice(8, 4), currentBigEndian ^ chunkIndex.Value);
    }
}

public sealed class SessionChunkCipher : IDisposable
{
    public const int TagSizeBytes = 16;

    private readonly AesGcm _gcm;
    private readonly BaseNonce _baseNonce;
    private readonly object _sync = new();

    public SessionChunkCipher(TransferKey key, BaseNonce baseNonce)
    {
        _gcm = new AesGcm(key.Span, TagSizeBytes);
        _baseNonce = baseNonce;
    }

    public int EncryptChunkInto(ReadOnlySpan<byte> plaintext, ChunkIndex index, Span<byte> destination)
    {
        int totalLength = plaintext.Length + TagSizeBytes;
        if (destination.Length < totalLength)
            throw new ArgumentException("Destination buffer is too small for ciphertext + tag.", nameof(destination));

        Span<byte> nonce = stackalloc byte[12];
        ChunkNonce.DeriveInto(_baseNonce, index, nonce);

        Span<byte> ciphertext = destination[..plaintext.Length];
        Span<byte> tag = destination.Slice(plaintext.Length, TagSizeBytes);

        lock (_sync)
        {
            _gcm.Encrypt(nonce, plaintext, ciphertext, tag);
        }

        return totalLength;
    }

    public int DecryptChunkInto(ReadOnlySpan<byte> encryptedWithTag, ChunkIndex index, Span<byte> destination)
    {
        if (encryptedWithTag.Length < TagSizeBytes)
            throw new CryptographicException("Encrypted chunk data is too short to contain authentication tag.");

        int cipherLength = encryptedWithTag.Length - TagSizeBytes;
        if (destination.Length < cipherLength)
            throw new ArgumentException("Destination buffer is too small for decrypted plaintext.", nameof(destination));

        Span<byte> nonce = stackalloc byte[12];
        ChunkNonce.DeriveInto(_baseNonce, index, nonce);

        ReadOnlySpan<byte> ciphertext = encryptedWithTag[..cipherLength];
        ReadOnlySpan<byte> tag = encryptedWithTag[cipherLength..];
        Span<byte> plaintext = destination[..cipherLength];

        lock (_sync)
        {
            _gcm.Decrypt(nonce, ciphertext, tag, plaintext);
        }

        return cipherLength;
    }

    public void Dispose() => _gcm.Dispose();
}

public static class ChunkCryptoService
{
    public const int TagSizeBytes = 16;

    public static EncryptedChunkData EncryptChunk(
        PlaintextChunkData plaintext,
        TransferKey key,
        BaseNonce baseNonce,
        ChunkIndex index)
    {
        byte[] combined = new byte[plaintext.Length + TagSizeBytes];
        EncryptChunkInto(plaintext.Span, key, baseNonce, index, combined);
        return EncryptedChunkData.FromMemory(combined);
    }

    public static int EncryptChunkInto(
        ReadOnlySpan<byte> plaintext,
        TransferKey key,
        BaseNonce baseNonce,
        ChunkIndex index,
        Span<byte> destination)
    {
        int totalLength = plaintext.Length + TagSizeBytes;
        if (destination.Length < totalLength)
            throw new ArgumentException("Destination buffer is too small for ciphertext + tag.", nameof(destination));

        Span<byte> nonce = stackalloc byte[12];
        ChunkNonce.DeriveInto(baseNonce, index, nonce);

        Span<byte> ciphertext = destination[..plaintext.Length];
        Span<byte> tag = destination.Slice(plaintext.Length, TagSizeBytes);

        using AesGcm gcm = new(key.Span, TagSizeBytes);
        gcm.Encrypt(nonce, plaintext, ciphertext, tag);
        return totalLength;
    }

    public static PlaintextChunkData DecryptChunk(
        EncryptedChunkData encrypted,
        TransferKey key,
        BaseNonce baseNonce,
        ChunkIndex index)
    {
        if (encrypted.Length < TagSizeBytes)
            throw new CryptographicException("Encrypted chunk data is too short to contain authentication tag.");

        int cipherLength = encrypted.Length - TagSizeBytes;
        byte[] plaintext = new byte[cipherLength];
        DecryptChunkInto(encrypted.Span, key, baseNonce, index, plaintext);
        return PlaintextChunkData.FromMemory(plaintext);
    }

    public static int DecryptChunkInto(
        ReadOnlySpan<byte> encryptedWithTag,
        TransferKey key,
        BaseNonce baseNonce,
        ChunkIndex index,
        Span<byte> destination)
    {
        if (encryptedWithTag.Length < TagSizeBytes)
            throw new CryptographicException("Encrypted chunk data is too short to contain authentication tag.");

        int cipherLength = encryptedWithTag.Length - TagSizeBytes;
        if (destination.Length < cipherLength)
            throw new ArgumentException("Destination buffer is too small for decrypted plaintext.", nameof(destination));

        Span<byte> nonce = stackalloc byte[12];
        ChunkNonce.DeriveInto(baseNonce, index, nonce);

        ReadOnlySpan<byte> ciphertext = encryptedWithTag[..cipherLength];
        ReadOnlySpan<byte> tag = encryptedWithTag[cipherLength..];
        Span<byte> plaintext = destination[..cipherLength];

        using AesGcm gcm = new(key.Span, TagSizeBytes);
        gcm.Decrypt(nonce, ciphertext, tag, plaintext);
        return cipherLength;
    }
}
