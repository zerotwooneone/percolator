namespace Percolator.Desktop.Udp.Interfaces;

public interface IBroadcaster
{
    Task Broadcast(byte[] data, CancellationToken cancellationToken);
}