using Percolator.Apps.FileTransfer.Domain.Exceptions;

namespace Percolator.Apps.FileTransfer.Domain.Common;

public readonly record struct ChunkByteLength(int Bytes)
{
    public const int MaxChunkBytes = 4 * 1024 * 1024; // 4 MB

    public static ChunkByteLength FromBytes(int bytes)
    {
        if (bytes < 1 || bytes > MaxChunkBytes)
            throw new DomainValidationException($"Chunk byte length {bytes} must be between 1 and {MaxChunkBytes} bytes.");
        return new ChunkByteLength(bytes);
    }

    public ChunkByteLength Subtract(TransferByteLength length)
    {
        int remaining = Bytes - length.Bytes;
        if (remaining < 0)
            throw new DomainValidationException("Remaining chunk byte length cannot be negative.");
        return new ChunkByteLength(remaining);
    }
}
