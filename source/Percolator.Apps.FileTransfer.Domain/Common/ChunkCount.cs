using Percolator.Apps.FileTransfer.Domain.Exceptions;

namespace Percolator.Apps.FileTransfer.Domain.Common;

public readonly record struct ChunkCount(int Count)
{
    public static ChunkCount FromInt(int count)
    {
        if (count < 1)
            throw new DomainValidationException("Total chunk count must be at least 1.");
        return new ChunkCount(count);
    }
}
