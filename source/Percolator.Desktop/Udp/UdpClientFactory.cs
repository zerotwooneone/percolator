using System.Collections.Concurrent;
using Percolator.Desktop.Udp.Interfaces;
using R3;

namespace Percolator.Desktop.Udp;

public class UdpClientFactory
{
    private readonly ConcurrentDictionary<int, UdpWrapper> _wrappersByPort = new();
    private readonly FrameProvider _receiveContext=new NewThreadSleepFrameProvider();
    public IBroadcaster CreateBroadcaster(int port)
    {
         var result = _wrappersByPort.GetOrAdd(port, ClientFactory);
         //todo: handle old or errored out clients
         return result;
    }
    public ISender GetOrCreateSender(int port)
    {
        var result = _wrappersByPort.GetOrAdd(port, ClientFactory);
        //todo: handle old or errored out clients
        return result;
    }
    public IListener CreateListener(int port)
    {
        var result = _wrappersByPort.GetOrAdd(port, ClientFactory);
        //todo: handle old or errored out clients
        return result;
    }

    private UdpWrapper ClientFactory(int p)
    {
        return new UdpWrapper(p, _receiveContext);
    }
}