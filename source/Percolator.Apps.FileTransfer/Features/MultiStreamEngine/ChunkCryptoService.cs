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
        baseNonce.Span.CopyTo(derived);

        int currentBigEndian = BinaryPrimitives.ReadInt32BigEndian(derived.AsSpan(8, 4));
        BinaryPrimitives.WriteInt32BigEndian(derived.AsSpan(8, 4), currentBigEndian ^ chunkIndex.Value);

        return new ChunkNonce(derived);
    }
}

public static class ChunkCryptoService
{
    public static EncryptedChunkData EncryptChunk(
        PlaintextChunkData plaintext,
        TransferKey key,
        BaseNonce baseNonce,
        ChunkIndex index)
    {
        ChunkNonce nonce = ChunkNonce.Derive(baseNonce, index);
        byte[] ciphertext = new byte[plaintext.Length];
        byte[] tag = new byte[16];

        using AesGcm gcm = new(key.Span, 16);
        gcm.Encrypt(nonce.Span, plaintext.Span, ciphertext, tag);

        byte[] combined = new byte[ciphertext.Length + 16];
        ciphertext.CopyTo(combined, 0);
        tag.CopyTo(combined, ciphertext.Length);

        return EncryptedChunkData.FromBytes(combined);
    }

    public static PlaintextChunkData DecryptChunk(
        EncryptedChunkData encrypted,
        TransferKey key,
        BaseNonce baseNonce,
        ChunkIndex index)
    {
        if (encrypted.Length < 16)
            throw new CryptographicException("Encrypted chunk data is too short to contain authentication tag.");

        ChunkNonce nonce = ChunkNonce.Derive(baseNonce, index);
        int cipherLength = encrypted.Length - 16;
        ReadOnlySpan<byte> ciphertext = encrypted.Span[..cipherLength];
        ReadOnlySpan<byte> tag = encrypted.Span[cipherLength..];

        byte[] plaintext = new byte[cipherLength];
        using AesGcm gcm = new(key.Span, 16);
        gcm.Decrypt(nonce.Span, ciphertext, tag, plaintext);

        return PlaintextChunkData.FromBytes(plaintext);
    }
}
