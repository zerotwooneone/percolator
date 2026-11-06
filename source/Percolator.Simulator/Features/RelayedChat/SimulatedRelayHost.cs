using System;
using System.IO;
using System.Net;
using System.Security.Authentication;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Percolator.Domain.Common;
using Percolator.Domain.Delivery.Hosting;
using Percolator.Domain.Delivery.Ports;
using Percolator.Domain.Security.Ports;
using Percolator.Infrastructure.Persistence;
using Percolator.Infrastructure.Persistence.Repositories;
using Percolator.Infrastructure.Plugins.BlobStorage;
using Percolator.Infrastructure.Security;
using Percolator.Infrastructure.Transport;
using Percolator.Infrastructure.Transport.Endpoints;
#if WINDOWS
using Percolator.Infrastructure.Windows.Security;
#endif
using Percolator.Simulator.Features.Logging;
using Percolator.Simulator.Features.Shell;
using Percolator.Simulator.Features.Topology;

namespace Percolator.Simulator.Features.RelayedChat;

internal sealed class SimulatedRelayHost : ISimulatedRelayHost
{
    private readonly ISimulatorLogSink _logSink;
    private readonly ISimulatorStateBus _stateBus;
    private readonly string _relayTempDirectory;
    private readonly SimulatorNotifyingRelayCapabilityStoreDecorator _capabilityStore;
    private SimulatorNotifyingRelayPreKeyRepositoryDecorator? _preKeyRepository;
    private PercolatorDbContext? _dbContext;
    private WebApplication? _app;
    private bool _isRunning;
    private bool _disposed;

    public SimulatedNodeId Id { get; }
    public string DisplayName { get; }
    public int BoundPort { get; private set; }
    public Uri BoundEndpoint => new($"https://127.0.0.1:{BoundPort}");
    public IServiceProvider Services => _app?.Services ?? throw new InvalidOperationException("Relay host not started.");

    public IRelayMailboxCapabilityStore MailboxStore => _capabilityStore;
    public IRelayPreKeyRepository PreKeyRepository => _preKeyRepository ?? throw new InvalidOperationException("Relay host not started.");
    public IRelayQueueChaosController ChaosController => _capabilityStore;

    public int TotalHeldEnvelopeCount => _capabilityStore.TotalEnvelopeCount;
    public int TotalHostedOpkCount => _preKeyRepository?.GetTotalHostedOpkCount() ?? 0;

    public SimulatedRelayHost(
        SimulatedNodeId id,
        string displayName,
        int port,
        ISimulatorLogSink logSink,
        ISimulatorStateBus stateBus)
    {
        Id = id;
        DisplayName = displayName;
        BoundPort = port;
        _logSink = logSink ?? throw new ArgumentNullException(nameof(logSink));
        _stateBus = stateBus ?? throw new ArgumentNullException(nameof(stateBus));

        _relayTempDirectory = Path.Combine(Path.GetTempPath(), "PercolatorSim", $"relay_{Id.Value:N}");
        Directory.CreateDirectory(_relayTempDirectory);

        _capabilityStore = new SimulatorNotifyingRelayCapabilityStoreDecorator(_stateBus, Id);
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        if (_isRunning) return;

#if !WINDOWS
        if (OperatingSystem.IsWindows())
        {
            var errorMsg = "Relay host cannot start: Running on Windows under the generic 'net10.0' build target without Windows SChannel TLS infrastructure. " +
                           "Please launch under the Windows target ('net10.0-windows10.0.19041.0').";
            _logSink.Log(new SimulatorLogEntry(
                DateTimeOffset.UtcNow,
                LogLevel.Critical,
                DisplayName,
                Id,
                "PlatformGuard",
                errorMsg));
            throw new PlatformNotSupportedException(errorMsg);
        }
#endif

        ITransportCertificateProvider certProvider;
#if WINDOWS
        certProvider = new WindowsSChannelCertificateProvider(Path.Combine(_relayTempDirectory, "Certs"));
#else
        certProvider = new InMemoryTransportCertificateProvider();
#endif

        var cert = await certProvider.GetOrCreateServerCertificateAsync(ct);

        // 1. Initialize SQLite DbContext for Relay
        var dbPath = Path.Combine(_relayTempDirectory, "relay.db");
        var dbOptions = new DbContextOptionsBuilder<PercolatorDbContext>()
            .UseSqlite($"Data Source={dbPath}")
            .AddInterceptors(new SimulatorSqlCipherPragmaInterceptor())
            .Options;

        _dbContext = new PercolatorDbContext(dbOptions);
        try
        {
            var creator = _dbContext.GetService<IRelationalDatabaseCreator>();
            await creator.CreateTablesAsync(ct);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 1)
        {
            // Tables already created
        }

        var sqlitePreKeyRepo = new SqliteRelayPreKeyRepository(_dbContext);
        _preKeyRepository = new SimulatorNotifyingRelayPreKeyRepositoryDecorator(sqlitePreKeyRepo, _stateBus, Id);

        var profileRepo = new SqliteRelayProfileRepository(_dbContext);
        var chunkStore = new LocalFileChunkStore(Path.Combine(_relayTempDirectory, "Chunks"));
        var ledgerRepo = new SqliteRelayLedgerRepository(_dbContext);
        var cryptoEngine = new SignalCryptoEngine();
        var zkEngine = new ZkgroupCryptographyService();

        // 2. Build Kestrel WebApplication with gRPC endpoints
        var builder = WebApplication.CreateBuilder();

        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Listen(IPAddress.Loopback, BoundPort, listenOptions =>
            {
                listenOptions.Protocols = HttpProtocols.Http2;
                listenOptions.UseHttps(cert, httpsOptions =>
                {
                    httpsOptions.SslProtocols = SslProtocols.Tls13;
                });
            });
        });

        builder.Services.AddGrpc();
        builder.Services.AddLogging(lb =>
        {
            lb.ClearProviders();
            lb.SetMinimumLevel(LogLevel.Warning);
        });

        var unauthEndpoint = new UnauthenticatedDeliveryEndpoint(profileRepo, _preKeyRepository, chunkStore, _capabilityStore);
        var authEndpoint = new AuthenticatedRelayEndpoint(_capabilityStore, _preKeyRepository, cryptoEngine);
        var groupEndpoint = new AnonymousGroupRelayEndpoint(ledgerRepo, zkEngine);

        builder.Services.AddSingleton(unauthEndpoint);
        builder.Services.AddSingleton(authEndpoint);
        builder.Services.AddSingleton(groupEndpoint);

        _app = builder.Build();
        _app.MapGrpcService<UnauthenticatedDeliveryEndpoint>();
        _app.MapGrpcService<AuthenticatedRelayEndpoint>();
        _app.MapGrpcService<AnonymousGroupRelayEndpoint>();

        await _app.StartAsync(ct);
        _isRunning = true;

        _logSink.Log(new SimulatorLogEntry(
            DateTimeOffset.UtcNow,
            LogLevel.Information,
            DisplayName,
            Id,
            "RelayLifecycle",
            $"Relay host started and listening on https://127.0.0.1:{BoundPort}"));
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        if (!_isRunning || _app == null) return;
        _isRunning = false;

        try
        {
            await _app.StopAsync(ct);
            await _app.DisposeAsync();
        }
        catch { }
        finally
        {
            _app = null;
        }

        if (_dbContext != null)
        {
            try
            {
                await _dbContext.DisposeAsync();
            }
            catch { }
            finally
            {
                _dbContext = null;
            }
        }

        _logSink.Log(new SimulatorLogEntry(
            DateTimeOffset.UtcNow,
            LogLevel.Information,
            DisplayName,
            Id,
            "RelayLifecycle",
            $"Relay host '{DisplayName}' stopped."));
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        await StopAsync();

        try
        {
            if (Directory.Exists(_relayTempDirectory))
            {
                Directory.Delete(_relayTempDirectory, recursive: true);
            }
        }
        catch { }
    }
}
