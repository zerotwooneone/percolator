using Percolator.Apps.FileTransfer.Domain.Exceptions;

namespace Percolator.Apps.FileTransfer.Domain.Common;

public readonly record struct CompletedChunkCount(int Count)
{
    public static CompletedChunkCount Zero => new(0);

    public static CompletedChunkCount FromInt(int count)
    {
        if (count < 0)
            throw new DomainValidationException("Completed chunk count cannot be negative.");
        return new CompletedChunkCount(count);
    }
}
