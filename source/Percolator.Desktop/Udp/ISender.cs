using System.Net;

namespace Percolator.Desktop.Udp;

public interface ISender
{
    Task Send(IPAddress destination, byte[] data, CancellationToken cancellationToken);
}