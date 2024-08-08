using System.Collections.Concurrent;
using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;
using Percolator.Desktop.Data;
using R3;

namespace Percolator.Desktop.Main;

public class RemoteClientRepository : IRemoteClientRepository,IRemoteClientInitializer
{
    public Observable<ByteString> ClientAdded { get; }
    private readonly Subject<ByteString> _clientAdded = new();
    private readonly ConcurrentDictionary<ByteString, ClientWrapper> _clientsByIdentity= new();
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly FrameProvider _dbIoSyncContext;

    public RemoteClientRepository(
        IServiceScopeFactory serviceScopeFactory)
    {
        _dbIoSyncContext = new NewThreadSleepFrameProvider();
        _serviceScopeFactory = serviceScopeFactory;
        ClientAdded = _clientAdded.AsObservable();
        
        _clientAdded
            .ObserveOn(_dbIoSyncContext)
            .Subscribe(OnAnnouncerAdded);
    }
    
    public void AddKnownAnnouncers(IEnumerable<RemoteClientModel> announcerModels)
    {
        foreach (var model in announcerModels)
        {
            if (_clientsByIdentity.TryAdd(model.Identity, new ClientWrapper(model)))
            {
                _clientAdded.OnNext(model.Identity);
            }
        }
    }

    public RemoteClientModel? GetClientByIdentity(ByteString identity)
    {
        return !_clientsByIdentity.TryGetValue(identity, out var clientWrapper) ? null : clientWrapper.Client;
    }

    public RemoteClientModel GetOrAdd(ByteString identity, Func<ByteString, RemoteClientModel> addCallback)
    {
        return _clientsByIdentity.GetOrAdd(identity, b => new ClientWrapper(addCallback(b))).Client;
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
            //.ObserveOn(_dbIoSyncContext)
            .SelectAwait(async (_, _) =>
            {
                using var scope = _serviceScopeFactory.CreateScope();
                var persistenceService = scope.ServiceProvider.GetRequiredService<IPersistenceService>();
                await persistenceService.UpdateAnnouncer(remoteClient.Identity, remoteClient.PreferredNickname.Value,remoteClient.LastSeen.Value);
                return Unit.Default;
            })
            .Subscribe();
    }
    
    private void OnAnnouncerAdded(ByteString announcerId)
    {
        using var scope = _serviceScopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var announcer = _clientsByIdentity[announcerId].Client;
        var remoteClient = new RemoteClient();
        remoteClient.SetIdentity( announcer.Identity.ToByteArray());
        
        if (dbContext.RemoteClients.Any(client => client.Identity ==remoteClient.Identity))
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(announcer.PreferredNickname.CurrentValue))
        {
            remoteClient.PreferredNickname = announcer.PreferredNickname.CurrentValue;
        }
        foreach (var ipAddress in announcer.IpAddresses)
        {
            var ipString = ipAddress.ToString();
            remoteClient.RemoteClientIps.Add(new RemoteClientIp
            {
                IpAddress = ipString,
                RemoteClient = remoteClient
            });
        }

        dbContext.RemoteClients.Add(remoteClient);
        dbContext.SaveChanges();
    }
    
    private class ClientWrapper
    {
        public RemoteClientModel Client { get; }
        public Observable<Unit> DoDbUpdate { get; }
        public ClientWrapper(RemoteClientModel client)
        {
            Client = client;
            var propertyChanged = client.LastSeen
                .Skip(1)
                .Select(_=>Unit.Default)
                .Merge(client.PreferredNickname
                    .Skip(1)
                    .Select(_=>Unit.Default));
            DoDbUpdate = Observable.Interval(TimeSpan.FromSeconds(2))
                .CombineLatest(propertyChanged, (_, _) => Unit.Default)
                .Publish()
                .RefCount();
        }
    }
}