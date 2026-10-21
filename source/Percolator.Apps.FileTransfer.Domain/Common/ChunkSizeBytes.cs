using Percolator.Apps.FileTransfer.Domain.Exceptions;

namespace Percolator.Apps.FileTransfer.Domain.Common;

public readonly record struct ChunkSizeBytes(int Bytes)
{
    public const int MinChunkSize = 64 * 1024;        // 64 KB
    public const int MaxChunkSize = 4 * 1024 * 1024;   // 4 MB
    public const int DefaultChunkSize = 1024 * 1024;   // 1 MB

    public static ChunkSizeBytes Default => new(DefaultChunkSize);

    public static ChunkSizeBytes FromBytes(int bytes)
    {
        if (bytes < MinChunkSize || bytes > MaxChunkSize)
            throw new DomainValidationException($"Chunk size {bytes} is outside allowed range [{MinChunkSize}..{MaxChunkSize}].");
        return new ChunkSizeBytes(bytes);
    }

    public static ChunkSizeBytes SelectOptimalForBundle(TotalBundleBytes totalBytes)
    {
        if (totalBytes.Bytes <= 256L * 1024 * 1024)
            return new ChunkSizeBytes(256 * 1024);      // 256 KB (<= 1,024 chunks, fits in 48 KB inline envelope)
        if (totalBytes.Bytes <= 1024L * 1024 * 1024)
            return new ChunkSizeBytes(1024 * 1024);     // 1 MB (<= 1,024 chunks, fits in 48 KB inline envelope)
        if (totalBytes.Bytes <= 64L * 1024 * 1024 * 1024)
            return new ChunkSizeBytes(2 * 1024 * 1024); // 2 MB (<= 32,768 chunks)
        return new ChunkSizeBytes(MaxChunkSize);        // 4 MB for multi-hundred-GB / TB bundles
    }
}
