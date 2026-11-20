using Percolator.Apps.FileTransfer.Domain.Exceptions;

namespace Percolator.Apps.FileTransfer.Domain.Common;

public readonly record struct FileCount(int Count)
{
    public static FileCount FromInt(int count)
    {
        if (count < 1)
            throw new DomainValidationException("Total file count must be at least 1.");
        return new FileCount(count);
    }
}
