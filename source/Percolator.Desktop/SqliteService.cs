using System.IO;
using System.Net;
using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Percolator.Desktop.Data;
using Percolator.Desktop.Main;
using R3;

namespace Percolator.Desktop;

internal class SqliteService : IHostedService
{
    private readonly IRemoteClientInitializer _remoteClientInitializer;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<SqliteService> _logger;
    private readonly IServiceScopeFactory _serviceScopeFactory;

    public SqliteService(
        IRemoteClientInitializer remoteClientInitializer,
        ILoggerFactory loggerFactory,
        ILogger<SqliteService> logger,
        IServiceScopeFactory serviceScopeFactory)
    {
        _remoteClientInitializer = remoteClientInitializer;
        _loggerFactory = loggerFactory;
        _logger = logger;
        _serviceScopeFactory = serviceScopeFactory;
    }
    
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using var scope = _serviceScopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var dbPath = Directory.GetParent(dbContext.DbPath).FullName;
        Directory.CreateDirectory(dbPath);
        await dbContext.Database.MigrateAsync(cancellationToken);

        var selfRows = (from s in dbContext.SelfRows
            select s).Take(2).ToArray();
        if (selfRows.Length >= 2)
        {
            _logger.LogWarning("too many self rows");
        }

        Self self;
        if (selfRows.Length == 0)
        {
            var newSelf = new Self
            {
                IdentitySuffix = Convert.ToBase64String(Guid.NewGuid().ToByteArray()),
            };
            dbContext.SelfRows.Add(newSelf);
            await dbContext.SaveChangesAsync(cancellationToken);
            self=newSelf;
        }
        else
        {
            self=selfRows[0];
        }
        var models = dbContext.RemoteClients
            .Include(c=>c.RemoteClientIps)
            .ToArray()
            .SelectMany(dm =>
            {
                if(!dm.TryGetIdentityBytes(out var bytes))
                {
                    _logger.LogError("failed to get identity from remote client. id:{RemoteClientId}", dm.Id);
                    return Array.Empty<RemoteClientModel>();
                }

                var announcerModel = new RemoteClientModel(ByteString.CopyFrom(bytes), _loggerFactory.CreateLogger<RemoteClientModel>());
                foreach (var remoteClientIp in dm.RemoteClientIps)
                {
                    if (!IPAddress.TryParse(remoteClientIp.IpAddress, out var ipAddress))
                    {
                        _logger.LogError("failed to parse ip address. id:{RemoteClientId}", dm.Id);
                        continue;
                    }
                    announcerModel.AddIpAddress(ipAddress);
                }

                if (announcerModel.IpAddresses.Count != 0)
                {
                    announcerModel.SelectIpAddress(announcerModel.IpAddresses.Count-1);
                }

                if (!string.IsNullOrWhiteSpace(dm.PreferredNickname) )
                {
                    announcerModel.PreferredNickname.Value = dm.PreferredNickname;
                }

                if (dm.LastSeenUtc > 0)
                {
                    announcerModel.LastSeen.Value = dm.GetLocalLastSeen();
                }
                return new [] {announcerModel};
            }).ToArray();
        _remoteClientInitializer.AddKnownAnnouncers(models);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }
}