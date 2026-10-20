using Percolator.Apps.FileTransfer.Domain.Exceptions;

namespace Percolator.Apps.FileTransfer.Domain.Common;

public readonly record struct FileIndex(int Value)
{
    public static FileIndex FromInt(int value)
    {
        if (value < 0)
            throw new DomainValidationException("File index cannot be negative.");
        return new FileIndex(value);
    }
}
