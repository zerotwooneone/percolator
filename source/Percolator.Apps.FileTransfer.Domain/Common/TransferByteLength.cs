using Percolator.Apps.FileTransfer.Domain.Exceptions;

namespace Percolator.Apps.FileTransfer.Domain.Common;

public readonly record struct TransferByteLength(int Bytes)
{
    public static TransferByteLength Zero => new(0);

    public static TransferByteLength FromBytes(int bytes)
    {
        if (bytes < 0)
            throw new DomainValidationException("Transfer byte length cannot be negative.");
        return new TransferByteLength(bytes);
    }
}
