using Percolator.Apps.FileTransfer.Domain.Exceptions;

namespace Percolator.Apps.FileTransfer.Domain.Common;

public readonly record struct TotalBundleBytes(long Bytes)
{
    public static TotalBundleBytes Zero => new(0);

    public static TotalBundleBytes FromBytes(long bytes)
    {
        if (bytes < 0)
            throw new DomainValidationException("Total bundle bytes cannot be negative.");
        return new TotalBundleBytes(bytes);
    }
}
