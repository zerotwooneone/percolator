using Percolator.Apps.FileTransfer.Domain.Exceptions;

namespace Percolator.Apps.FileTransfer.Domain.Common;

public readonly record struct ChunkCount(int Count)
{
    public static ChunkCount Zero => new(0);

    public static ChunkCount FromInt(int count)
    {
        if (count < 0)
            throw new DomainValidationException("Chunk count cannot be negative.");
        return new ChunkCount(count);
    }
}
