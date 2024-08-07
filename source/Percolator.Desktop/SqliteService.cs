using System.IO;
using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Percolator.Desktop.Data;
using Percolator.Desktop.Main;

namespace Percolator.Desktop;

internal class SqliteService : IHostedService
{
    private readonly IAnnouncerInitializer _announcerInitializer;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ApplicationDbContext _dbContext;

    public SqliteService(ApplicationDbContext dbContext,
        IAnnouncerInitializer announcerInitializer,
        ILoggerFactory loggerFactory)
    {
        _announcerInitializer = announcerInitializer;
        _loggerFactory = loggerFactory;
        _dbContext = dbContext;
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
                return Array.Empty<AnnouncerModel>();
            }

            return new []{new AnnouncerModel(ByteString.CopyFrom(bytes), _loggerFactory.CreateLogger<AnnouncerModel>())};
        }));
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }
}