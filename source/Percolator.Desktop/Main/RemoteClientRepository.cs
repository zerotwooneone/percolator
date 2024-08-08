using System.Collections.Concurrent;
using Google.Protobuf;
using R3;

namespace Percolator.Desktop.Main;

public class RemoteClientRepository : IRemoteClientRepository,IRemoteClientInitializer
{
    public Observable<ByteString> ClientAdded { get; }
    private readonly Subject<ByteString> _clientAdded = new();
    private readonly ConcurrentDictionary<ByteString, RemoteClientModel> _clientsByIdentity= new();
    public IReadOnlyDictionary<ByteString, RemoteClientModel> RemoteClients => _clientsByIdentity;

    public RemoteClientRepository()
    {
        ClientAdded = _clientAdded.AsObservable();
    }
    
    public void AddKnownAnnouncers(IEnumerable<RemoteClientModel> announcerModels)
    {
        foreach (var model in announcerModels)
        {
            if (_clientsByIdentity.TryAdd(model.Identity, model))
            {
                _clientAdded.OnNext(model.Identity);
            }
        }
    }

    public RemoteClientModel GetOrAdd(ByteString identity, Func<ByteString, RemoteClientModel> addCallback)
    {
        return _clientsByIdentity.GetOrAdd(identity, addCallback);
    }

    public void OnNext(ByteString identity)
    {
        _clientAdded.OnNext(identity);
    }
}