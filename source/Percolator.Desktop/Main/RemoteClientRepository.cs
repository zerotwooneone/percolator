using System.Collections.Concurrent;
using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
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
            var identity = model.Identity;
            if (!_clientsByIdentity.TryAdd(identity, new ClientWrapper(model)))
            {
                continue;
            }

            _clientsByIdentity[identity].DoUpdate
                .SubscribeAwait(async (_,c)=>await OnUpdate(identity,c));
            _clientAdded.OnNext(identity);
        }
    }

    public RemoteClientModel? GetClientByIdentity(ByteString identity)
    {
        return !_clientsByIdentity.TryGetValue(identity, out var clientWrapper) ? null : clientWrapper.Client;
    }

    public RemoteClientModel GetOrAdd(ByteString identity, Func<ByteString, RemoteClientModel> addCallback)
    {
        return _clientsByIdentity.GetOrAdd(identity, b =>
        {
            var clientWrapper = new ClientWrapper(addCallback(b));
            clientWrapper.DoUpdate
                .SubscribeAwait(async (_,c)=>await OnUpdate(identity,c));
            return clientWrapper;
        }).Client;
    }
    
    public void OnNext(ByteString identity)
    {
        _clientAdded.OnNext(identity);
    }
    
    private async ValueTask OnUpdate(ByteString identity, CancellationToken cancellationToken)
    {
        var rc  = _clientsByIdentity[identity].Client;
        await UpdateAnnouncer(identity, rc.PreferredNickname.Value,rc.LastSeen.Value,cancellationToken);
    }
    
    public async Task UpdateAnnouncer(ByteString identity, string nickname, DateTimeOffset lastSeenValue,
        CancellationToken cancellationToken)
    {
        using var scope = _serviceScopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var rc = await dbContext.RemoteClients.FirstOrDefaultAsync(
            client => client.Identity == identity.ToBase64(), cancellationToken: cancellationToken);
        if (rc == null)
        {
            return;
        }

        rc.PreferredNickname = nickname;
        rc.SetLastSeen(lastSeenValue);
        await dbContext.SaveChangesAsync(cancellationToken);
    }
    
    public IDisposable WatchForChanges(RemoteClientModel remoteClient)
    {
        var identity = remoteClient.Identity;
        return _clientsByIdentity[identity].PropertyChanged
            .Take(1)
            //.ObserveOn(_dbIoSyncContext)
            .Subscribe(_ =>
            {
                _clientsByIdentity[identity].RequestUpdate();
            });
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
        private readonly Subject<Unit> _updateRequest;
        public RemoteClientModel Client { get; }
        public Observable<Unit> DoUpdate { get; }
        public Observable<Unit> PropertyChanged { get;}
        public ClientWrapper(RemoteClientModel client)
        {
            Client = client;
            PropertyChanged = client.LastSeen
                .Skip(1)
                .Select(_=>Unit.Default)
                .Merge(client.PreferredNickname
                    .Skip(1)
                    .Select(_=>Unit.Default))
                .Publish()
                .RefCount();
            _updateRequest = new Subject<Unit>();
            DoUpdate = _updateRequest
                .ThrottleFirst(TimeSpan.FromSeconds(2),timeProvider: TimeProvider.System)
                .Publish()
                .RefCount();
        }

        public void RequestUpdate()
        {
            _updateRequest.OnNext(Unit.Default);
        }
    }
}