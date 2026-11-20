using Percolator.Apps.FileTransfer.Domain.Exceptions;

namespace Percolator.Apps.FileTransfer.Domain.Common;

public readonly record struct PeerCount(int Count)
{
    public static PeerCount Zero => new(0);

    public static PeerCount FromInt(int count)
    {
        if (count < 0)
            throw new DomainValidationException("Peer count cannot be negative.");
        return new PeerCount(count);
    }
}
