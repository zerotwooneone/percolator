using System.Buffers.Binary;
using System.Security.Cryptography;
using Percolator.Application2.Ports;
using Percolator.Domain.Common;
using Percolator.Domain.Security.Ports;

namespace Percolator.Application2IntegrationTests.Infrastructure;

public sealed class ScenarioBlobCryptoService : IBlobCryptoService
{
    public const int ChunkSize = 64 * 1024; // 64 KB chunks
    public const int TagSize = 16;          // 16-byte AES-GCM tag

    private readonly ICryptoEngine _cryptoEngine;

    public ScenarioBlobCryptoService(ICryptoEngine? cryptoEngine = null)
    {
        _cryptoEngine = cryptoEngine ?? new ScenarioCryptoEngine();
    }

    public async ValueTask<DomainResult<EncryptedBlobPackage>> EncryptAndPackageAsync(
        Stream plaintextStream,
        string mimeType,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plaintextStream);

        using var memory = new MemoryStream();
        await plaintextStream.CopyToAsync(memory, ct).ConfigureAwait(false);
        var plaintextBytes = memory.ToArray();
        long plaintextSize = plaintextBytes.Length;

        var key = new byte[32];
        var baseNonce = new byte[12];
        RandomNumberGenerator.Fill(key);
        RandomNumberGenerator.Fill(baseNonce);

        using var ciphertextMs = new MemoryStream();

        int offset = 0;
        int chunkIndex = 0;

        Span<byte> aad = stackalloc byte[4];
        Span<byte> chunkNonce = stackalloc byte[12];

        while (offset < plaintextBytes.Length)
        {
            int remaining = plaintextBytes.Length - offset;
            int currentChunkLen = Math.Min(remaining, ChunkSize);

            var chunkPlaintext = plaintextBytes.AsSpan(offset, currentChunkLen);

            baseNonce.CopyTo(chunkNonce);
            BinaryPrimitives.WriteUInt32BigEndian(chunkNonce[8..12], (uint)chunkIndex);
            BinaryPrimitives.WriteUInt32BigEndian(aad, (uint)chunkIndex);

            var encryptedChunk = _cryptoEngine.EncryptAesGcm(key, chunkNonce, chunkPlaintext.ToArray(), aad.ToArray());
            ciphertextMs.Write(encryptedChunk);

            offset += currentChunkLen;
            chunkIndex++;
        }

        var ciphertextBytes = ciphertextMs.ToArray();
        var ciphertextSha256 = SHA256.HashData(ciphertextBytes);

        var package = new EncryptedBlobPackage(
            new MemoryStream(ciphertextBytes),
            ciphertextSha256,
            key,
            baseNonce,
            plaintextSize,
            ciphertextBytes.Length);

        return DomainResult<EncryptedBlobPackage>.Success(package);
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

        using var memory = new MemoryStream();
        await ciphertextStream.CopyToAsync(memory, ct).ConfigureAwait(false);
        var ciphertextBytes = memory.ToArray();

        using var plaintextMs = new MemoryStream();

        int offset = 0;
        int chunkIndex = 0;

        Span<byte> aad = stackalloc byte[4];
        Span<byte> chunkNonce = stackalloc byte[12];

        try
        {
            while (offset < ciphertextBytes.Length)
            {
                int remaining = ciphertextBytes.Length - offset;
                int wireChunkLen = Math.Min(remaining, ChunkSize + TagSize);
                if (wireChunkLen < TagSize)
                {
                    return DomainResult<Stream>.Failure(new DomainError(
                        "AEAD_VERIFICATION_FAILED", "Malformed ciphertext chunk: smaller than tag size."));
                }

                var chunkCiphertextAndTag = ciphertextBytes.AsSpan(offset, wireChunkLen);

                baseNonce.CopyTo(chunkNonce);
                BinaryPrimitives.WriteUInt32BigEndian(chunkNonce[8..12], (uint)chunkIndex);
                BinaryPrimitives.WriteUInt32BigEndian(aad, (uint)chunkIndex);

                var decryptedChunk = _cryptoEngine.DecryptAesGcm(encryptionKey, chunkNonce, chunkCiphertextAndTag.ToArray(), aad.ToArray());
                plaintextMs.Write(decryptedChunk);

                offset += wireChunkLen;
                chunkIndex++;
            }
        }
        catch (CryptographicException ex)
        {
            return DomainResult<Stream>.Failure(new DomainError(
                "AEAD_VERIFICATION_FAILED", $"Authentication tag verification failed: {ex.Message}"));
        }

        var decryptedBytes = plaintextMs.ToArray();
        if (decryptedBytes.Length > unpaddedPlaintextSize)
        {
            decryptedBytes = decryptedBytes.AsSpan(0, (int)unpaddedPlaintextSize).ToArray();
        }

        return DomainResult<Stream>.Success(new MemoryStream(decryptedBytes));
    }
}
