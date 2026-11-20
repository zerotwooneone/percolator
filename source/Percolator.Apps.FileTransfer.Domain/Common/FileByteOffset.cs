using Percolator.Apps.FileTransfer.Domain.Exceptions;

namespace Percolator.Apps.FileTransfer.Domain.Common;

public readonly record struct FileByteOffset(long Offset)
{
    public static FileByteOffset Zero => new(0);

    public static FileByteOffset FromOffset(long offset)
    {
        if (offset < 0)
            throw new DomainValidationException("File byte offset cannot be negative.");
        return new FileByteOffset(offset);
    }

    public FileByteOffset Add(TransferByteLength length) => new(Offset + length.Bytes);
}
