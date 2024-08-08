using System.Collections.Concurrent;
using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;
using R3;

namespace Percolator.Desktop.Main;

public class RemoteClientRepository : IRemoteClientRepository,IRemoteClientInitializer
{
    public Observable<ByteString> ClientAdded { get; }
    private readonly Subject<ByteString> _clientAdded = new();
    private readonly ConcurrentDictionary<ByteString, RemoteClientModel> _clientsByIdentity= new();
    public IReadOnlyDictionary<ByteString, RemoteClientModel> RemoteClients => _clientsByIdentity;
    private readonly IServiceScopeFactory _serviceScopeFactory;

    public RemoteClientRepository(
        IServiceScopeFactory serviceScopeFactory)
    {
        _serviceScopeFactory = serviceScopeFactory;
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
    
    public IDisposable WatchForChanges(RemoteClientModel remoteClient)
    {
        return remoteClient.PreferredNickname
            .Skip(1)
            .Take(1)
            .Select(_=>Unit.Default)
            .Amb(remoteClient.LastSeen
                .Skip(1)
                .Take(1)
                .Select(_=>Unit.Default))
            .SelectAwait(async (_, _) =>
            {
                using var scope = _serviceScopeFactory.CreateScope();
                var persistenceService = scope.ServiceProvider.GetRequiredService<IPersistenceService>();
                await persistenceService.UpdateAnnouncer(remoteClient.Identity, remoteClient.PreferredNickname.Value,remoteClient.LastSeen.Value);
                return Unit.Default;
            })
            .Subscribe();
    }
}