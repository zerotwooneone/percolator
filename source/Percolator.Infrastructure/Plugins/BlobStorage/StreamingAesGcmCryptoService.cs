using Percolator.Application.Ports;

namespace Percolator.Infrastructure.Plugins.BlobStorage;

using System;
using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Percolator.Domain.Common;

public sealed class StreamingAesGcmCryptoService : IBlobCryptoService
{
    private const int ChunkSize = 64 * 1024; // 64 KB
    private const int TagSize = 16;
    private const int NonceSize = 12;
    private const int BaseNonceSize = 8;
    private const long MaxBlobSizeBytes = 100L * 1024 * 1024; // 100 MB ceiling
    private const long InMemoryThresholdBytes = 1L * 1024 * 1024; // 1 MB: below this, use MemoryStream; above, use FileOptions.DeleteOnClose temp file

    public async ValueTask<DomainResult<EncryptedBlobPackage>> EncryptAndPackageAsync(
        Stream plaintextStream,
        string mimeType,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plaintextStream);

        if (plaintextStream.CanSeek && plaintextStream.Length > MaxBlobSizeBytes)
        {
            return DomainResult<EncryptedBlobPackage>.Failure(new DomainError(
                "BLOB_TOO_LARGE", $"Payload of size {plaintextStream.Length} bytes exceeds the 100 MB ceiling."));
        }

        // 1. Scrub metadata (EXIF/GPS/device info) into a bounded staging stream
        using var scrubbedStream = CreateStagingStream(plaintextStream.CanSeek ? plaintextStream.Length : 0);
        await ScrubMetadataAsync(plaintextStream, scrubbedStream, mimeType, ct).ConfigureAwait(false);
        scrubbedStream.Position = 0;

        if (scrubbedStream.Length > MaxBlobSizeBytes)
        {
            return DomainResult<EncryptedBlobPackage>.Failure(new DomainError(
                "BLOB_TOO_LARGE", $"Scrubbed payload exceeds the 100 MB ceiling."));
        }

        long plaintextSize = scrubbedStream.Length;

        // 2. Generate ephemeral cryptographic keys
        byte[] encryptionKey = RandomNumberGenerator.GetBytes(32);
        byte[] baseNonce = RandomNumberGenerator.GetBytes(BaseNonceSize);

        var ciphertextStream = CreateStagingStream(plaintextSize + ((plaintextSize / ChunkSize) + 1) * TagSize);
        using var incrementalSha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var aesGcm = new AesGcm(encryptionKey, TagSize);

        byte[] plainBuffer = new byte[ChunkSize];
        byte[] cipherBuffer = new byte[ChunkSize];
        byte[] tagBuffer = new byte[TagSize];
        byte[] nonceBuffer = new byte[NonceSize];

        Buffer.BlockCopy(baseNonce, 0, nonceBuffer, 0, BaseNonceSize);

        uint chunkIndex = 0;
        int bytesRead;

        try
        {
            while ((bytesRead = await scrubbedStream.ReadAsync(plainBuffer.AsMemory(0, ChunkSize), ct).ConfigureAwait(false)) > 0)
            {
                bool isFinal = scrubbedStream.Position >= scrubbedStream.Length;
                byte[] aad = new byte[] { isFinal ? (byte)0x01 : (byte)0x00 };

                BinaryPrimitives.WriteUInt32BigEndian(nonceBuffer.AsSpan(BaseNonceSize), chunkIndex++);

                var plaintextSlice = plainBuffer.AsSpan(0, bytesRead);
                var ciphertextSlice = cipherBuffer.AsSpan(0, bytesRead);
                var tagSpan = tagBuffer.AsSpan(0, TagSize);

                aesGcm.Encrypt(nonceBuffer, plaintextSlice, ciphertextSlice, tagSpan, aad);

                await ciphertextStream.WriteAsync(cipherBuffer.AsMemory(0, bytesRead), ct).ConfigureAwait(false);
                await ciphertextStream.WriteAsync(tagBuffer.AsMemory(0, TagSize), ct).ConfigureAwait(false);

                incrementalSha256.AppendData(cipherBuffer, 0, bytesRead);
                incrementalSha256.AppendData(tagBuffer, 0, TagSize);
            }

            // Handle empty stream edge-case
            if (chunkIndex == 0)
            {
                BinaryPrimitives.WriteUInt32BigEndian(nonceBuffer.AsSpan(BaseNonceSize), 0);
                byte[] aad = new byte[] { 0x01 };
                aesGcm.Encrypt(nonceBuffer, ReadOnlySpan<byte>.Empty, Span<byte>.Empty, tagBuffer, aad);

                await ciphertextStream.WriteAsync(tagBuffer.AsMemory(0, TagSize), ct).ConfigureAwait(false);
                incrementalSha256.AppendData(tagBuffer, 0, TagSize);
            }

            ciphertextStream.Position = 0;
            byte[] ciphertextHash = incrementalSha256.GetHashAndReset();

            return DomainResult<EncryptedBlobPackage>.Success(new EncryptedBlobPackage(
                CiphertextStream: ciphertextStream,
                CiphertextSha256: ciphertextHash,
                EncryptionKey: encryptionKey,
                BaseNonce: baseNonce,
                PlaintextSize: plaintextSize,
                CiphertextSize: ciphertextStream.Length));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plainBuffer);
            CryptographicOperations.ZeroMemory(cipherBuffer);
            CryptographicOperations.ZeroMemory(tagBuffer);
            CryptographicOperations.ZeroMemory(nonceBuffer);
        }
    }

    public async ValueTask<DomainResult<Stream>> DecryptStreamAsync(
        Stream ciphertextStream,
        byte[] encryptionKey,
        byte[] baseNonce,
        long unpaddedPlaintextSize,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ciphertextStream);
        ArgumentNullException.ThrowIfNull(encryptionKey);
        ArgumentNullException.ThrowIfNull(baseNonce);

        if (encryptionKey.Length != 32)
        {
            return DomainResult<Stream>.Failure(new DomainError(
                "INVALID_KEY_LENGTH", "Encryption key must be exactly 32 bytes."));
        }

        if (baseNonce.Length != BaseNonceSize)
        {
            return DomainResult<Stream>.Failure(new DomainError(
                "INVALID_NONCE_LENGTH", $"Base nonce must be {BaseNonceSize} bytes."));
        }

        var plaintextStream = CreateStagingStream(unpaddedPlaintextSize);
        using var aesGcm = new AesGcm(encryptionKey, TagSize);

        byte[] cipherBuffer = new byte[ChunkSize];
        byte[] plainBuffer = new byte[ChunkSize];
        byte[] tagBuffer = new byte[TagSize];
        byte[] nonceBuffer = new byte[NonceSize];

        Buffer.BlockCopy(baseNonce, 0, nonceBuffer, 0, BaseNonceSize);

        uint chunkIndex = 0;
        bool finalChunkProcessed = false;

        try
        {
            while (ciphertextStream.Position < ciphertextStream.Length)
            {
                long remaining = ciphertextStream.Length - ciphertextStream.Position;
                if (remaining < TagSize)
                {
                    return DomainResult<Stream>.Failure(new DomainError(
                        "AUTHENTICATION_FAILED", "Ciphertext stream truncated; incomplete tag block."));
                }

                int expectedChunkCiphertext = (int)Math.Min(ChunkSize, remaining - TagSize);
                int readCipher = await ReadExactAsync(ciphertextStream, cipherBuffer, expectedChunkCiphertext, ct).ConfigureAwait(false);
                int readTag = await ReadExactAsync(ciphertextStream, tagBuffer, TagSize, ct).ConfigureAwait(false);

                if (readCipher != expectedChunkCiphertext || readTag != TagSize)
                {
                    return DomainResult<Stream>.Failure(new DomainError(
                        "AUTHENTICATION_FAILED", "Unexpected EOF while reading chunk."));
                }

                bool isFinal = ciphertextStream.Position >= ciphertextStream.Length;
                byte[] aad = new byte[] { isFinal ? (byte)0x01 : (byte)0x00 };

                BinaryPrimitives.WriteUInt32BigEndian(nonceBuffer.AsSpan(BaseNonceSize), chunkIndex++);

                try
                {
                    aesGcm.Decrypt(
                        nonceBuffer,
                        cipherBuffer.AsSpan(0, expectedChunkCiphertext),
                        tagBuffer.AsSpan(0, TagSize),
                        plainBuffer.AsSpan(0, expectedChunkCiphertext),
                        aad);
                }
                catch (CryptographicException)
                {
                    return DomainResult<Stream>.Failure(new DomainError(
                        "AUTHENTICATION_FAILED", $"Authentication failed on chunk {chunkIndex - 1}."));
                }

                await plaintextStream.WriteAsync(plainBuffer.AsMemory(0, expectedChunkCiphertext), ct).ConfigureAwait(false);

                if (isFinal)
                {
                    finalChunkProcessed = true;
                    break;
                }
            }

            if (!finalChunkProcessed && ciphertextStream.Length > 0)
            {
                return DomainResult<Stream>.Failure(new DomainError(
                    "AUTHENTICATION_FAILED", "Final block flag not reached; stream may be truncated."));
            }

            plaintextStream.Position = 0;
            return DomainResult<Stream>.Success(plaintextStream);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(cipherBuffer);
            CryptographicOperations.ZeroMemory(plainBuffer);
            CryptographicOperations.ZeroMemory(tagBuffer);
            CryptographicOperations.ZeroMemory(nonceBuffer);
        }
    }

    private static Stream CreateStagingStream(long estimatedSize)
    {
        if (estimatedSize > 0 && estimatedSize <= InMemoryThresholdBytes)
        {
            return new MemoryStream((int)estimatedSize);
        }

        if (estimatedSize == 0)
        {
            return new MemoryStream();
        }

        string tempPath = Path.Combine(Path.GetTempPath(), $"perc_stage_{Guid.NewGuid():N}.tmp");
        return new FileStream(
            tempPath,
            FileMode.Create,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: ChunkSize,
            FileOptions.DeleteOnClose);
    }

    private static async Task ScrubMetadataAsync(Stream source, Stream destination, string mimeType, CancellationToken ct)
    {
        string normMime = mimeType?.ToLowerInvariant() ?? string.Empty;

        if (normMime.Contains("jpeg") || normMime.Contains("jpg"))
        {
            if (await TryScrubJpegAsync(source, destination, ct).ConfigureAwait(false))
            {
                return;
            }
            source.Position = 0;
            destination.SetLength(0);
        }
        else if (normMime.Contains("png"))
        {
            if (await TryScrubPngAsync(source, destination, ct).ConfigureAwait(false))
            {
                return;
            }
            source.Position = 0;
            destination.SetLength(0);
        }

        // ISO 14971 Clause 7.4 Risk-Benefit Decision:
        // Complex multimedia containers (MP4, MKV) require full container demuxing and recalculation of sample chunk offset
        // tables (stco/co64 atoms) to avoid media playback corruption. Per Risk-Benefit Analysis, bundling heavyweight
        // native demuxers (FFmpeg) introduces disproportionate attack surface and platform dependencies.
        // Complex media containers stream without in-memory re-muxing as an accepted residual risk.
        await source.CopyToAsync(destination, ct).ConfigureAwait(false);
    }

    private static async Task<bool> TryScrubJpegAsync(Stream input, Stream output, CancellationToken ct)
    {
        byte[] header = new byte[2];
        if (await input.ReadAsync(header.AsMemory(0, 2), ct).ConfigureAwait(false) != 2)
            return false;

        if (header[0] != 0xFF || header[1] != 0xD8)
            return false;

        await output.WriteAsync(header.AsMemory(0, 2), ct).ConfigureAwait(false);

        byte[] markerBuffer = new byte[2];
        byte[] lengthBuffer = new byte[2];

        while (input.Position < input.Length)
        {
            int read = await input.ReadAsync(markerBuffer.AsMemory(0, 2), ct).ConfigureAwait(false);
            if (read != 2) break;

            if (markerBuffer[0] != 0xFF)
                return false;

            byte marker = markerBuffer[1];

            if (marker == 0xDA || marker == 0xD9)
            {
                await output.WriteAsync(markerBuffer.AsMemory(0, 2), ct).ConfigureAwait(false);
                await input.CopyToAsync(output, ct).ConfigureAwait(false);
                return true;
            }

            if (marker == 0xD8 || (marker >= 0xD0 && marker <= 0xD7) || marker == 0x01)
            {
                await output.WriteAsync(markerBuffer.AsMemory(0, 2), ct).ConfigureAwait(false);
                continue;
            }

            if (await input.ReadAsync(lengthBuffer.AsMemory(0, 2), ct).ConfigureAwait(false) != 2)
                return false;

            ushort length = BinaryPrimitives.ReadUInt16BigEndian(lengthBuffer);
            if (length < 2) return false;

            int payloadLength = length - 2;

            if (marker == 0xE1 || marker == 0xE2 || marker == 0xED)
            {
                input.Seek(payloadLength, SeekOrigin.Current);
            }
            else
            {
                await output.WriteAsync(markerBuffer.AsMemory(0, 2), ct).ConfigureAwait(false);
                await output.WriteAsync(lengthBuffer.AsMemory(0, 2), ct).ConfigureAwait(false);

                byte[] payload = new byte[payloadLength];
                if (await ReadExactAsync(input, payload, payloadLength, ct).ConfigureAwait(false) != payloadLength)
                    return false;

                await output.WriteAsync(payload.AsMemory(0, payloadLength), ct).ConfigureAwait(false);
            }
        }

        return true;
    }

    private static async Task<bool> TryScrubPngAsync(Stream input, Stream output, CancellationToken ct)
    {
        byte[] pngSig = new byte[8];
        if (await input.ReadAsync(pngSig.AsMemory(0, 8), ct).ConfigureAwait(false) != 8)
            return false;

        ReadOnlySpan<byte> expectedSig = stackalloc byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        if (!pngSig.AsSpan().SequenceEqual(expectedSig))
            return false;

        await output.WriteAsync(pngSig.AsMemory(0, 8), ct).ConfigureAwait(false);

        byte[] lenBuffer = new byte[4];
        byte[] typeBuffer = new byte[4];
        byte[] crcBuffer = new byte[4];

        while (input.Position < input.Length)
        {
            if (await input.ReadAsync(lenBuffer.AsMemory(0, 4), ct).ConfigureAwait(false) != 4)
                break;
            if (await input.ReadAsync(typeBuffer.AsMemory(0, 4), ct).ConfigureAwait(false) != 4)
                return false;

            uint chunkLen = BinaryPrimitives.ReadUInt32BigEndian(lenBuffer);
            string type = System.Text.Encoding.ASCII.GetString(typeBuffer);

            if (type is "eXIf" or "tEXt" or "zTXt" or "iTXt")
            {
                input.Seek(chunkLen + 4, SeekOrigin.Current);
            }
            else
            {
                await output.WriteAsync(lenBuffer.AsMemory(0, 4), ct).ConfigureAwait(false);
                await output.WriteAsync(typeBuffer.AsMemory(0, 4), ct).ConfigureAwait(false);

                byte[] data = new byte[chunkLen];
                if (await ReadExactAsync(input, data, (int)chunkLen, ct).ConfigureAwait(false) != (int)chunkLen)
                    return false;

                await output.WriteAsync(data.AsMemory(0, (int)chunkLen), ct).ConfigureAwait(false);

                if (await input.ReadAsync(crcBuffer.AsMemory(0, 4), ct).ConfigureAwait(false) != 4)
                    return false;

                await output.WriteAsync(crcBuffer.AsMemory(0, 4), ct).ConfigureAwait(false);

                if (type == "IEND")
                    break;
            }
        }

        return true;
    }

    private static async Task<int> ReadExactAsync(Stream stream, byte[] buffer, int count, CancellationToken ct)
    {
        int totalRead = 0;
        while (totalRead < count)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(totalRead, count - totalRead), ct).ConfigureAwait(false);
            if (read == 0) break;
            totalRead += read;
        }
        return totalRead;
    }
}
