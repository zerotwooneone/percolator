using Percolator.Desktop.Udp;
using Percolator.Desktop.Udp.Interfaces;

namespace Percolator.Desktop.Main;

public class MainService
{
    private readonly UdpClientFactory _udpClientFactory;
    private readonly IBroadcaster _broadcaster;
    private readonly IListener _listener;
    public const int BroadcastPort = 12345;

    public MainService(UdpClientFactory udpClientFactory)
    {
        _udpClientFactory = udpClientFactory;
        _broadcaster = _udpClientFactory.CreateBroadcaster(BroadcastPort);
        _listener = _udpClientFactory.CreateListener(BroadcastPort);
    }
    public Task Listen(CancellationToken cancellationToken)
    {
        _listener.Listen(cancellationToken);
        
        return Task.CompletedTask;
    }
}