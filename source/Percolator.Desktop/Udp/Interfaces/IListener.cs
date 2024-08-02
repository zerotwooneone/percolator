using System.Net.Sockets;
using R3;

namespace Percolator.Desktop.Udp.Interfaces;

public interface IListener
{
    ReactiveProperty<bool> IsListening { get; }
    Observable<UdpReceiveResult> Received { get; }
}