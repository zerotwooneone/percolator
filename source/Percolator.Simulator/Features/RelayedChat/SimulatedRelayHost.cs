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
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.Ports;
using Percolator.Infrastructure.Persistence;
using Percolator.Infrastructure.Persistence.Repositories;
using Percolator.Infrastructure.Plugins.BlobStorage;
using Percolator.Infrastructure.Security;
using Percolator.Infrastructure.Common;
using Percolator.Infrastructure.Transport;
using Percolator.Infrastructure.Transport.Endpoints;
#if WINDOWS
using Percolator.Infrastructure.Windows.Security;
#endif
using Percolator.Simulator.Features.Logging;
using Percolator.Simulator.Features.Shell;
using Percolator.Simulator.Features.Topology;

namespace Percolator.Simulator.Features.RelayedChat;

internal sealed class SimulatorRelayDbContextFactory : IDbContextFactory<PercolatorDbContext>
{
    private readonly DbContextOptions<PercolatorDbContext> _options;

    public SimulatorRelayDbContextFactory(DbContextOptions<PercolatorDbContext> options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public PercolatorDbContext CreateDbContext() => new(_options);
}

internal sealed class SimulatedRelayHost : ISimulatedRelayHost
{
    private readonly ISimulatorLogSink _logSink;
    private readonly ISimulatorStateBus _stateBus;
    private readonly string _relayTempDirectory;
    private readonly SimulatorNotifyingRelayCapabilityStoreDecorator _capabilityStore;
    private SimulatorNotifyingRelayPreKeyRepositoryDecorator? _preKeyRepository;
    private SqliteRelayLedgerRepository? _ledgerRepository;
    private SimulatorRelayQueryService? _queryService;
    private WebApplication? _app;
    private bool _isRunning;
    private bool _disposed;

    public SimulatedNodeId Id { get; }
    public string DisplayName { get; }
    public int BoundPort { get; private set; }
    public Uri BoundEndpoint => new($"https://127.0.0.1:{BoundPort}");
    public IServiceProvider Services => _app?.Services ?? throw new InvalidOperationException("Relay host not started.");
    public PublicIdentityId RelayIdentityId => new(Id.Value);

    public IRelayMailboxCapabilityStore MailboxStore => _capabilityStore;
    public ISimulatorRelayQueryService QueryService => _queryService ?? throw new InvalidOperationException("Relay host not started.");
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

        // 1. Initialize SQLite DbContext factory for Relay
        var dbPath = Path.Combine(_relayTempDirectory, "relay.db");
        var dbOptions = new DbContextOptionsBuilder<PercolatorDbContext>()
            .UseSqlite($"Data Source={dbPath}")
            .AddInterceptors(new SimulatorSqlCipherPragmaInterceptor())
            .Options;

        var contextFactory = new SimulatorRelayDbContextFactory(dbOptions);

        using (var initContext = contextFactory.CreateDbContext())
        {
            try
            {
                var creator = initContext.GetService<IRelationalDatabaseCreator>();
                await creator.CreateTablesAsync(ct);
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 1)
            {
                // Tables already created
            }
        }

        _queryService = new SimulatorRelayQueryService(contextFactory);

        var sqlitePreKeyRepo = new SqliteRelayPreKeyRepository(contextFactory);
        _preKeyRepository = new SimulatorNotifyingRelayPreKeyRepositoryDecorator(sqlitePreKeyRepo, _stateBus, Id);

        var profileRepo = new SqliteRelayProfileRepository(contextFactory);
        var chunkStore = new LocalFileChunkStore(Path.Combine(_relayTempDirectory, "Chunks"));
        var ledgerRepo = new SqliteRelayLedgerRepository(contextFactory);
        _ledgerRepository = ledgerRepo;
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
        var timeProvider = new SystemDateTimeProvider();
        var groupEndpoint = new AnonymousGroupRelayEndpoint(ledgerRepo, zkEngine, _capabilityStore, timeProvider);

        builder.Services.AddSingleton<IRelayLedgerRepository>(ledgerRepo);
        builder.Services.AddSingleton<IRelayPreKeyRepository>(_preKeyRepository);
        builder.Services.AddSingleton<IRelayMailboxCapabilityStore>(_capabilityStore);
        builder.Services.AddSingleton<IDbContextFactory<PercolatorDbContext>>(contextFactory);
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

    public async Task ProvisionGroupLedgerAsync(RelayGroupLedger ledger, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        if (_ledgerRepository == null)
        {
            throw new InvalidOperationException("Relay host not started.");
        }

        await _ledgerRepository.SaveAsync(ledger, ct);
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        if (!_isRunning || _app == null) return;

        await _app.StopAsync(ct);
        await _app.DisposeAsync();
        _app = null;
        _isRunning = false;

        _logSink.Log(new SimulatorLogEntry(
            DateTimeOffset.UtcNow,
            LogLevel.Information,
            DisplayName,
            Id,
            "RelayLifecycle",
            "Relay host stopped."));
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        if (_app != null)
        {
            await _app.StopAsync().ConfigureAwait(false);
            await _app.DisposeAsync().ConfigureAwait(false);
            _app = null;
        }

        try
        {
            if (Directory.Exists(_relayTempDirectory))
            {
                Directory.Delete(_relayTempDirectory, recursive: true);
            }
        }
        catch
        {
            // Best effort cleanup
        }
    }

    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
