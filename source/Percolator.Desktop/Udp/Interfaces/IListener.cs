using System.Net.Sockets;
using R3;

namespace Percolator.Desktop.Udp.Interfaces;

public interface IListener
{
    Task Listen(CancellationToken cancellationToken);
    Observable<UdpReceiveResult> Received { get; }
}