namespace Percolator.Application.Network;

public interface IOpaqueMessageDeliverer
{
    Task<bool> DeliverAsync(byte[] opaqueBytes, CancellationToken ct);
}
