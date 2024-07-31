using System.Net;

namespace Percolator.Desktop.Udp;

public interface ISender
{
    Task Broadcast(IPAddress destination, byte[] data, CancellationToken cancellationToken);
}