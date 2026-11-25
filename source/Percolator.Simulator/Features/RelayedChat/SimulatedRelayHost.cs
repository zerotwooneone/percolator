using System;
using System.IO;
using System.Net;
using System.Security.Authentication;
using System.Threading;
using System.Threading.Tasks;
using Percolator.Application.Delivery.Ports;
using Percolator.Application.Hosting;
using Percolator.Application.Ports;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Percolator.Apps.Discovery.Features.PrivateRendezvous;
using Percolator.Apps.Discovery.Features.RelayDirectoryHosting;
using System.Linq;
using Percolator.Apps.Discovery.Ports;
using Percolator.Apps.FileTransfer.Domain.TransferNegotiation;
using Percolator.Domain.Common;
using Percolator.Domain.Delivery.Hosting;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Delivery.Ports;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.Ports;
using Percolator.Infrastructure.Adapters;
using Percolator.Infrastructure.Common;
using Percolator.Infrastructure.Persistence;
using Percolator.Infrastructure.Persistence.Repositories;
using Percolator.Infrastructure.Plugins.BlobStorage;
using Percolator.Infrastructure.Security;
using Percolator.Infrastructure.Transport;
using Percolator.Infrastructure.Transport.Endpoints;
using Percolator.Infrastructure.Vault;
#if WINDOWS
using Percolator.Infrastructure.Windows.Security;
#endif
using Percolator.Simulator.Features.Chaos;
using Percolator.Simulator.Features.Logging;
using Percolator.Simulator.Features.RichChatAndProfiles;
using Percolator.Simulator.Features.Shell;
using Percolator.Simulator.Features.Topology;
using PublicIdentityId = Percolator.Domain.Identities.PublicIdentityId;

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
    private readonly MasterKeyHandle _masterKeyHandle = MasterKeyHandle.GenerateRandom();
    private readonly ISimulatorLogSink _logSink;
    private readonly ISimulatorStateBus _stateBus;
    private readonly IVirtualClockStateService _virtualClock;
    private readonly ISimulatorChaosController _chaosController;
    private readonly IPortReservationService _portReservationService;
    private readonly string _relayTempDirectory;
    private SimulatorNotifyingRelayCapabilityStoreDecorator? _capabilityStore;
    private IRelayMailboxCapabilityStore? _chaosCapabilityStore;
    private SimulatorNotifyingRelayPreKeyRepositoryDecorator? _preKeyRepository;
    private SimulatorRelayQueryService? _queryService;
    private readonly RelayHostingPolicyHolder _policyHolder = new(RelayHostingPolicy.Default);
    private IExchangeRegistrationModerationService? _moderationService;
    private IRelayPeerDirectory? _peerDirectory;
    private IPrivateRendezvousService? _rendezvousService;
    private WebApplication? _app;
    private bool _isRunning;
    private bool _disposed;

    public SimulatedNodeId Id { get; }
    public string DisplayName { get; }
    public int BoundPort { get; private set; }
    public Uri BoundEndpoint { get; private set; }
    public RelayEndpoint Endpoint => RelayEndpoint.FromHostAndPort("127.0.0.1", BoundPort);
    public RelayHostingPolicy Policy
    {
        get => _policyHolder.GetPolicy();
        set => _policyHolder.UpdatePolicy(value);
    }
    public IServiceProvider Services => _app?.Services ?? throw new InvalidOperationException("Relay host not started.");
    public PublicIdentityId RelayIdentityId => new(Id.Value);

    public IRelayMailboxCapabilityStore MailboxStore => _chaosCapabilityStore ?? throw new InvalidOperationException("Relay host not started.");
    public ISimulatorRelayQueryService QueryService => _queryService ?? throw new InvalidOperationException("Relay host not started.");
    public IRelayPreKeyRepository PreKeyRepository => _preKeyRepository ?? throw new InvalidOperationException("Relay host not started.");
    public IRelayQueueChaosController ChaosController => _capabilityStore ?? throw new InvalidOperationException("Relay host not started.");
    public IRelayPeerDirectory PeerDirectory => _peerDirectory ?? throw new InvalidOperationException("Relay host not started.");
    public IPrivateRendezvousService RendezvousService => _rendezvousService ?? throw new InvalidOperationException("Relay host not started.");
    public IExchangeRegistrationModerationService ModerationService => _moderationService ?? throw new InvalidOperationException("Relay host not started.");

    public int TotalHeldEnvelopeCount => _capabilityStore?.TotalEnvelopeCount ?? 0;
    public int TotalHostedOpkCount => _preKeyRepository?.GetTotalHostedOpkCount() ?? 0;

    public SimulatedRelayHost(
        SimulatedNodeId id,
        string displayName,
        int port,
        ISimulatorLogSink logSink,
        ISimulatorStateBus stateBus,
        IVirtualClockStateService virtualClock,
        ISimulatorChaosController chaosController,
        IPortReservationService portReservationService)
    {
        Id = id;
        DisplayName = displayName;
        BoundPort = port;
        BoundEndpoint = new Uri($"percolator://127.0.0.1:{BoundPort}");
        _logSink = logSink ?? throw new ArgumentNullException(nameof(logSink));
        _stateBus = stateBus ?? throw new ArgumentNullException(nameof(stateBus));
        _virtualClock = virtualClock ?? throw new ArgumentNullException(nameof(virtualClock));
        _chaosController = chaosController ?? throw new ArgumentNullException(nameof(chaosController));
        _portReservationService = portReservationService ?? throw new ArgumentNullException(nameof(portReservationService));

        _relayTempDirectory = Path.Combine(Path.GetTempPath(), "PercolatorSim", $"relay_{Id.Value:N}");
        Directory.CreateDirectory(_relayTempDirectory);
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
            .AddInterceptors(new SqlCipherPragmaInterceptor(_masterKeyHandle, new VaultStorageOptions { UseEncryption = false }))
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

        _capabilityStore = new SimulatorNotifyingRelayCapabilityStoreDecorator(
            _stateBus,
            Id,
            new SqliteRelayMailboxCapabilityStore(contextFactory),
            contextFactory);

        _chaosCapabilityStore = new ChaosRelayMailboxCapabilityStoreDecorator(
            _capabilityStore,
            _chaosController,
            Id);

        _queryService = new SimulatorRelayQueryService(contextFactory);

        var sqlitePreKeyRepo = new SqliteRelayPreKeyRepository(contextFactory);
        _preKeyRepository = new SimulatorNotifyingRelayPreKeyRepositoryDecorator(sqlitePreKeyRepo, _stateBus, Id);

        var profileRepo = new SqliteRelayProfileRepository(contextFactory);
        var chunkStore = new LocalFileChunkStore(Path.Combine(_relayTempDirectory, "Chunks"));
        var ledgerRepo = new SqliteRelayLedgerRepository(contextFactory);
        var cryptoEngine = new SignalCryptoEngine(new CryptographyOptions());
        var zkEngine = new ZkgroupCryptographyService();

        // 2. Initialize Peer Directory and Private Rendezvous services using production PreKeyAvailabilityAdapter
        var admissionGate = new DiscoveryAdmissionGate();
        var preKeyAvailabilityAdapter = new PreKeyAvailabilityAdapter(_preKeyRepository);
        _peerDirectory = new SqliteRelayPeerDirectory(
            contextFactory,
            admissionGate,
            policy: RelayDiscoveryPolicy.Open,
            preKeyAvailabilityPort: preKeyAvailabilityAdapter,
            clock: _virtualClock,
            registrationTtl: TimeSpan.FromHours(24));

        _rendezvousService = new SqlitePrivateRendezvousService(
            contextFactory,
            clock: _virtualClock,
            entryTtl: TimeSpan.FromHours(48));

        // 3. Build Kestrel WebApplication with gRPC endpoints
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

        var localAssociationRepo = new SqliteLocalRelayAssociationRepository(contextFactory);
        var pendingRegistrationRepo = new SqlitePendingMailboxRegistrationRepository(contextFactory);
        var rateLimiter = new InMemoryAuthenticatedMemberRateLimiter(new AuthenticatedMemberRateLimiterOptions());
        _moderationService = new ExchangeRegistrationModerationService(pendingRegistrationRepo);

        var unauthEndpoint = new UnauthenticatedDeliveryEndpoint(profileRepo, chunkStore, _chaosCapabilityStore, _policyHolder);
        var authEndpoint = new AuthenticatedRelayEndpoint(
            _chaosCapabilityStore,
            _preKeyRepository,
            cryptoEngine,
            _policyHolder,
            localAssociationRepo,
            pendingRegistrationRepo,
            rateLimiter,
            RelayIdentityId);
        var timeProvider = new SystemDateTimeProvider();
        var groupEndpoint = new AnonymousGroupRelayEndpoint(ledgerRepo, zkEngine, _chaosCapabilityStore, timeProvider, RelayIdentityId);
        var nearbyEndpoint = new NearbyDiscoveryEndpoint(_peerDirectory, _chaosCapabilityStore, _preKeyRepository, _virtualClock, BoundPort, cryptoEngine);
        var rendezvousEndpoint = new PrivateRendezvousRelayEndpoint(_rendezvousService);

        builder.Services.AddSingleton<IRelayHostingPolicyProvider>(_policyHolder);
        builder.Services.AddSingleton<ILocalRelayAssociationRepository>(localAssociationRepo);
        builder.Services.AddSingleton<IPendingMailboxRegistrationRepository>(pendingRegistrationRepo);
        builder.Services.AddSingleton<IAuthenticatedMemberRateLimiter>(rateLimiter);
        builder.Services.AddSingleton<IExchangeRegistrationModerationService>(_moderationService);
        builder.Services.AddSingleton<IRelayLedgerRepository>(ledgerRepo);
        builder.Services.AddSingleton<IRelayPreKeyRepository>(_preKeyRepository);
        builder.Services.AddSingleton<IRelayMailboxCapabilityStore>(_chaosCapabilityStore);
        builder.Services.AddSingleton<IDbContextFactory<PercolatorDbContext>>(contextFactory);
        builder.Services.AddSingleton<IRelayPeerDirectory>(_peerDirectory);
        builder.Services.AddSingleton<IPrivateRendezvousService>(_rendezvousService);
        builder.Services.AddSingleton(unauthEndpoint);
        builder.Services.AddSingleton(authEndpoint);
        builder.Services.AddSingleton(groupEndpoint);
        builder.Services.AddSingleton(nearbyEndpoint);
        builder.Services.AddSingleton(rendezvousEndpoint);

        _app = builder.Build();

        _app.MapGrpcService<UnauthenticatedDeliveryEndpoint>();
        _app.MapGrpcService<AuthenticatedRelayEndpoint>();
        _app.MapGrpcService<AnonymousGroupRelayEndpoint>();
        _app.MapGrpcService<NearbyDiscoveryEndpoint>();
        _app.MapGrpcService<PrivateRendezvousRelayEndpoint>();

        await _app.StartAsync(ct);
        _isRunning = true;

        BoundEndpoint = new Uri($"percolator://127.0.0.1:{BoundPort}");

        _logSink.Log(new SimulatorLogEntry(
            DateTimeOffset.UtcNow,
            LogLevel.Information,
            DisplayName,
            Id,
            "RelayLifecycle",
            $"Relay host started and listening on percolator://127.0.0.1:{BoundPort}"));
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

        _masterKeyHandle.Dispose();
        _portReservationService.ReleasePort(BoundPort);

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
