using System.Collections.Concurrent;
using Percolator.Desktop.Udp.Interfaces;

namespace Percolator.Desktop.Udp;

public class UdpClientFactory
{
    private readonly ConcurrentDictionary<int, UdpWrapper> _wrappersByPort = new();
    public IBroadcaster CreateBroadcaster(int port)
    {
         var result = _wrappersByPort.GetOrAdd(port, p => new UdpWrapper(p));
         //todo: handle old or errored out clients
         return result;
    }
    public IListener CreateListener(int port)
    {
        var result = _wrappersByPort.GetOrAdd(port, p => new UdpWrapper(p));
        //todo: handle old or errored out clients
        return result;
    }
}