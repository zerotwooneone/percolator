using System.Net;
using System.Net.Sockets;
using Percolator.Desktop.Udp.Interfaces;
using R3;

namespace Percolator.Desktop.Udp;

public class UdpWrapper : IBroadcaster, IListener, ISender
{
    private readonly Subject<UdpReceiveResult> _received;
    public int Port { get; }
    public UdpClient UdpClient { get; }
    public ReactiveProperty<bool> IsListening { get; }
    public Observable<UdpReceiveResult> Received { get; }

    public UdpWrapper(int port)
    {
        IsListening = new ReactiveProperty<bool>(false);
        Port = port;
        UdpClient = new UdpClient();
        UdpClient.Client.Bind(new IPEndPoint(IPAddress.Any, port));
        UdpClient.EnableBroadcast = true;
        _received = new Subject<UdpReceiveResult>();
        var receivedConnectable = _received
            .AsObservable()
            .Publish()
            .RefCount();
        Received = receivedConnectable;
    }

    public async Task Broadcast(byte[] data, CancellationToken cancellationToken)
    {
        await UdpClient.SendAsync(
            data, 
            new IPEndPoint(IPAddress.Broadcast, 
                Port),
            cancellationToken); 
    }

    public Task Listen(CancellationToken cancellationToken)
    {
        if (IsListening.Value)
        {
            return Task.CompletedTask;
        }

        cancellationToken.Register(()=>
        {
            IsListening.Value = false;
        });

        var startNewReceive = new Subject<Unit>();

        //todo: see if this can be improved
        var frameProvider = new NewThreadSleepFrameProvider();
        
        startNewReceive
            .ObserveOn(frameProvider)
            .TakeUntil(cancellationToken)
            .SelectAwait((_,c) => UdpClient.ReceiveAsync(CancellationTokenSource.CreateLinkedTokenSource(c,cancellationToken).Token))
            .Subscribe(urr =>
            {
                startNewReceive.OnNext(Unit.Default);
                _received.OnNext(urr);
            });
        
        startNewReceive.OnNext(Unit.Default);

        IsListening.Value = !cancellationToken.IsCancellationRequested;
        return startNewReceive.LastAsync(cancellationToken);
    }

    public async Task Send(IPAddress destination, byte[] data, CancellationToken cancellationToken)
    {
        await UdpClient.SendAsync(
            data, 
            new IPEndPoint(destination, 
                Port),
            cancellationToken);
    }
}