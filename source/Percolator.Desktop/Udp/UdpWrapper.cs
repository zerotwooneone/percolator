using System.Net;
using System.Net.Sockets;
using Percolator.Desktop.Udp.Interfaces;
using R3;

namespace Percolator.Desktop.Udp;

public class UdpWrapper : IBroadcaster, IListener, ISender, IDisposable
{
    private readonly Subject<UdpReceiveResult> _received;
    public int Port { get; }
    private readonly UdpClient _udpClient;
    public ReactiveProperty<bool> IsListening { get; }
    public Observable<UdpReceiveResult> Received { get; }
    private readonly CancellationTokenSource _cancellationTokenSource = new();

    public UdpWrapper(int port,FrameProvider receiveContext)
    {
        IsListening = new ReactiveProperty<bool>(false);
        Port = port;
        _udpClient = new UdpClient();
        _udpClient.Client.Bind(new IPEndPoint(IPAddress.Any, port));
        _udpClient.EnableBroadcast = true;
        _received = new Subject<UdpReceiveResult>();
        var receivedConnectable = _received
            .AsObservable()
            .Publish()
            .RefCount();
        Received = receivedConnectable;
        
        var startNewReceive = new Subject<Unit>();

        startNewReceive
            .ObserveOn(receiveContext)
            .SelectAwait((_,c) => _udpClient.ReceiveAsync(CancellationTokenSource.CreateLinkedTokenSource(c,_cancellationTokenSource.Token).Token))
            .TakeUntil(_cancellationTokenSource.Token)
            .Subscribe(urr =>
            {
                startNewReceive.OnNext(Unit.Default);
                if (IsListening.Value)
                {
                    _received.OnNext(urr);   
                }
            });
        
        startNewReceive.OnNext(Unit.Default);
    }

    public async Task Broadcast(byte[] data, CancellationToken cancellationToken)
    {
        await _udpClient.SendAsync(
            data, 
            new IPEndPoint(IPAddress.Broadcast, 
                Port),
            cancellationToken); 
    }

    public async Task Send(IPAddress destination, byte[] data, CancellationToken cancellationToken)
    {
        await _udpClient.SendAsync(
            data, 
            new IPEndPoint(destination, 
                Port),
            cancellationToken);
    }

    public void Dispose()
    {
        _received.Dispose();
        _udpClient.Dispose();
        _cancellationTokenSource.Dispose();
        IsListening.Dispose();
    }
}