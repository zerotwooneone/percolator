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
}
