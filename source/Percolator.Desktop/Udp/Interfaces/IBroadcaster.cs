namespace Percolator.Desktop.Udp.Interfaces;

public interface IBroadcaster
{
    Task Broadcast(byte[] data);
}