using System.IO;
using System.Net;
using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Percolator.Desktop.Data;
using Percolator.Desktop.Main;
using R3;

namespace Percolator.Desktop;

internal class SqliteService : IHostedService
{
    private readonly IAnnouncerInitializer _announcerInitializer;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<SqliteService> _logger;
    private readonly ApplicationDbContext _dbContext;
    private readonly FrameProvider _dbIoSyncContext;

    public SqliteService(ApplicationDbContext dbContext,
        IAnnouncerInitializer announcerInitializer,
        ILoggerFactory loggerFactory,
        ILogger<SqliteService> logger)
    {
        _dbIoSyncContext = new NewThreadSleepFrameProvider();
        _announcerInitializer = announcerInitializer;
        _loggerFactory = loggerFactory;
        _logger = logger;
        _dbContext = dbContext;
        
        _announcerInitializer.AnnouncerAdded
            .ObserveOn(_dbIoSyncContext)
            .Subscribe(OnAnnouncerAdded);
    }
    
    private void OnAnnouncerAdded(ByteString announcerId)
    {
        var announcer = _announcerInitializer.Announcers[announcerId];
        var remoteClient = new RemoteClient();
        remoteClient.SetIdentity( announcer.Identity.ToByteArray());
        if (_dbContext.RemoteClients.Any(client => client.Identity ==remoteClient.Identity))
        {
            return;
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

        _dbContext.RemoteClients.Add(remoteClient);
        _dbContext.SaveChanges();
    }
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var dbPath = Directory.GetParent(_dbContext.DbPath).FullName;
        Directory.CreateDirectory(dbPath);
        await _dbContext.Database.MigrateAsync(cancellationToken);
        
        _announcerInitializer.AddKnownAnnouncers(_dbContext.RemoteClients.ToArray().SelectMany(dm =>
        {
            if(!dm.TryGetIdentityBytes(out var bytes))
            {
                _logger.LogError("failed to get identity from remote client. id:{RemoteClientId}", dm.Id);
                return Array.Empty<AnnouncerModel>();
            }

            var announcerModel = new AnnouncerModel(ByteString.CopyFrom(bytes), _loggerFactory.CreateLogger<AnnouncerModel>());
            foreach (var remoteClientIp in dm.RemoteClientIps)
            {
                if (!IPAddress.TryParse(remoteClientIp.IpAddress, out var ipAddress))
                {
                    _logger.LogError("failed to parse ip address. id:{RemoteClientId}", dm.Id);
                    continue;
                }
                announcerModel.AddIpAddress(ipAddress);
            }
            return new [] {announcerModel};
        }));
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }
}