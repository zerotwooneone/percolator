namespace Percolator.ApplicationIntegrationTests.Harness;

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Percolator.Application.Delivery.Ports;
using Percolator.Application.Hosting;
using Percolator.Application.Ports;
using Percolator.Domain.Common;
using Percolator.Domain.Delivery.Hosting;
using Percolator.Domain.Delivery.Ports;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities;
using Percolator.Domain.Identities.Ports;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.Ports;
using Percolator.Infrastructure.Common;
using Percolator.Infrastructure.Events;
using Percolator.Infrastructure.Security;
using Percolator.Infrastructure.Persistence;
using Percolator.Infrastructure.Persistence.Entities;
using Percolator.Infrastructure.Persistence.Repositories;
using Percolator.Infrastructure.Plugins.BlobStorage;
using Percolator.Infrastructure.Transport;
using Percolator.Infrastructure.Transport.Endpoints;
using Percolator.PluginSdk;
using Percolator.PluginSdk.Events;

public sealed class TestRelayServer : IAsyncDisposable
{
    private readonly SqliteConnection _keeperConnection;
    private readonly ServiceProvider _serviceProvider;
    private readonly KestrelPeerTransportServer _server;
    private readonly RelayHostingPolicyHolder _policyHolder;
    private readonly string _blobStorageDir;

    public PublicIdentityId RelayIdentityId { get; }
    public RelayEndpoint Endpoint { get; private set; } = null!;
    public RelayHostingPolicy Policy
    {
        get => _policyHolder.GetPolicy();
        set => _policyHolder.UpdatePolicy(value);
    }
    public IServiceProvider Services => _serviceProvider;

    private TestRelayServer(
        SqliteConnection keeperConnection,
        ServiceProvider serviceProvider,
        KestrelPeerTransportServer server,
        RelayHostingPolicyHolder policyHolder,
        string blobStorageDir,
        PublicIdentityId relayIdentityId)
    {
        _keeperConnection = keeperConnection;
        _serviceProvider = serviceProvider;
        _server = server;
        _policyHolder = policyHolder;
        _blobStorageDir = blobStorageDir;
        RelayIdentityId = relayIdentityId;
    }

    public static async Task<TestRelayServer> StartAsync(
        RelayHostingPolicy? initialPolicy = null,
        AuthenticatedMemberRateLimiterOptions? rateLimiterOptions = null)
    {
        SQLitePCL.Batteries_V2.Init();

        var connectionString = $"DataSource=file:relay_{Guid.NewGuid():N}?mode=memory&cache=shared";
        var keeperConnection = new SqliteConnection(connectionString);
        await keeperConnection.OpenAsync().ConfigureAwait(false);

        var dbOptions = new DbContextOptionsBuilder<PercolatorDbContext>()
            .UseSqlite(connectionString)
            .Options;

        await using (var initDb = new PercolatorDbContext(dbOptions))
        {
            await initDb.Database.EnsureCreatedAsync().ConfigureAwait(false);
        }

        var policyHolder = new RelayHostingPolicyHolder(initialPolicy ?? RelayHostingPolicy.Default);
        var blobDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Percolator",
            "TestRelayBlobs",
            Guid.NewGuid().ToString("N"));

        var relayIdentityId = PublicIdentityId.New();
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IDateTimeProvider, SystemDateTimeProvider>();
        services.AddSingleton<ITransportCertificateProvider, InMemoryTransportCertificateProvider>();
        services.AddSingleton<IRelayHostingPolicyProvider>(policyHolder);

        services.AddDbContextFactory<PercolatorDbContext>(b => b.UseSqlite(connectionString));
        services.AddScoped(sp => sp.GetRequiredService<IDbContextFactory<PercolatorDbContext>>().CreateDbContext());

        services.AddSingleton<IRelayMailboxCapabilityStore, SqliteRelayMailboxCapabilityStore>();
        services.AddScoped<IRelayPreKeyRepository>(sp => new SqliteRelayPreKeyRepository(sp.GetRequiredService<IDbContextFactory<PercolatorDbContext>>()));
        services.AddScoped<IRelayProfileRepository>(sp => new SqliteRelayProfileRepository(sp.GetRequiredService<PercolatorDbContext>()));
        services.AddScoped<IRelayLedgerRepository>(sp => new SqliteRelayLedgerRepository(sp.GetRequiredService<PercolatorDbContext>()));
        services.AddScoped<ILocalRelayAssociationRepository, SqliteLocalRelayAssociationRepository>();
        services.AddScoped<IPendingMailboxRegistrationRepository, SqlitePendingMailboxRegistrationRepository>();
        services.AddSingleton(rateLimiterOptions ?? new AuthenticatedMemberRateLimiterOptions());
        services.AddSingleton<IAuthenticatedMemberRateLimiter, InMemoryAuthenticatedMemberRateLimiter>();
        services.AddSingleton(new CryptographyOptions());
        services.AddSingleton<ICryptoEngine, SignalCryptoEngine>();
        services.AddSingleton<IZkProofEngine, ZkgroupCryptographyService>();

        services.AddSingleton<InMemoryEventBus>();
        services.AddSingleton<IEventBus>(sp => sp.GetRequiredService<InMemoryEventBus>());
        services.AddSingleton<IEventPublisher>(sp => sp.GetRequiredService<InMemoryEventBus>());
        services.AddSingleton<IEventSubscriber>(sp => sp.GetRequiredService<InMemoryEventBus>());

        services.AddScoped<IExchangeRegistrationModerationService, ExchangeRegistrationModerationService>();
        services.AddSingleton<ILocalChunkStore>(new LocalFileChunkStore(blobDir));

        services.AddScoped<UnauthenticatedDeliveryEndpoint>();
        services.AddSingleton<InMemorySwarmTrackerRegistry>();
        services.AddSingleton<InMemoryTransferPipeBroker>();
        services.AddSingleton(new UdpStunResponderOptions { BindAddress = IPAddress.Loopback, Port = 0 });
        services.AddSingleton<UdpStunResponderService>();
        services.AddSingleton<IUdpStunEndpointProvider>(sp => sp.GetRequiredService<UdpStunResponderService>());
        services.AddScoped<ExchangeSwarmTrackerEndpoint>();
        services.AddScoped<ExchangeTransferPipeEndpoint>();
        services.AddScoped(sp => new AuthenticatedRelayEndpoint(
            sp.GetRequiredService<IRelayMailboxCapabilityStore>(),
            sp.GetRequiredService<IRelayPreKeyRepository>(),
            sp.GetRequiredService<ICryptoEngine>(),
            sp.GetRequiredService<IRelayHostingPolicyProvider>(),
            sp.GetRequiredService<ILocalRelayAssociationRepository>(),
            sp.GetRequiredService<IPendingMailboxRegistrationRepository>(),
            sp.GetRequiredService<IAuthenticatedMemberRateLimiter>(),
            relayIdentityId));
        services.AddScoped(sp => new AnonymousGroupRelayEndpoint(
            sp.GetRequiredService<IRelayLedgerRepository>(),
            sp.GetRequiredService<IZkProofEngine>(),
            sp.GetRequiredService<IRelayMailboxCapabilityStore>(),
            sp.GetRequiredService<IDateTimeProvider>(),
            relayIdentityId));

        var serviceProvider = services.BuildServiceProvider();
        var certProvider = serviceProvider.GetRequiredService<ITransportCertificateProvider>();
        var stunService = serviceProvider.GetRequiredService<UdpStunResponderService>();
        await stunService.StartAsync(CancellationToken.None).ConfigureAwait(false);

        var server = new KestrelPeerTransportServer(certProvider, serviceProvider, new PeerTransportServerOptions());

        await server.StartAsync(IPAddress.Loopback, new PortNumber(0)).ConfigureAwait(false);
        var boundPort = server.BoundPort!.Value.Value;

        return new TestRelayServer(keeperConnection, serviceProvider, server, policyHolder, blobDir, relayIdentityId)
        {
            Endpoint = RelayEndpoint.FromHostAndPort("127.0.0.1", boundPort)
        };
    }

    public IPEndPoint? StunEndpoint => _serviceProvider.GetRequiredService<IUdpStunEndpointProvider>().BoundEndpoint;
    public InMemorySwarmTrackerRegistry SwarmTrackerRegistry => _serviceProvider.GetRequiredService<InMemorySwarmTrackerRegistry>();
    public InMemoryTransferPipeBroker TransferPipeBroker => _serviceProvider.GetRequiredService<InMemoryTransferPipeBroker>();
    public string BlobStorageDirectory => _blobStorageDir;

    public async Task StopAsync() => await _server.StopAsync().ConfigureAwait(false);

    public async Task RestartAsync()
    {
        await _server.StartAsync(IPAddress.Loopback, new PortNumber(Endpoint.Port!.Value)).ConfigureAwait(false);
    }

    public async Task<int> GetQueueDepthAsync(BlindedRoutingToken token)
    {
        var factory = _serviceProvider.GetRequiredService<IDbContextFactory<PercolatorDbContext>>();
        await using var db = await factory.CreateDbContextAsync().ConfigureAwait(false);
        var bytes = token.ToByteArray();
        return await db.RelayQueuedEnvelopes.CountAsync(e => e.RoutingToken == bytes).ConfigureAwait(false);
    }

    public async Task<int> GetAvailableOneTimePreKeyCountAsync(BlindedRoutingToken token)
    {
        var factory = _serviceProvider.GetRequiredService<IDbContextFactory<PercolatorDbContext>>();
        await using var db = await factory.CreateDbContextAsync().ConfigureAwait(false);
        var bytes = token.ToByteArray();
        return await db.RelayOneTimePreKeys.CountAsync(k => k.OwnerRoutingToken == bytes).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<RelayOneTimePreKeyEntity>> GetStoredOneTimePreKeysAsync(BlindedRoutingToken token)
    {
        var factory = _serviceProvider.GetRequiredService<IDbContextFactory<PercolatorDbContext>>();
        await using var db = await factory.CreateDbContextAsync().ConfigureAwait(false);
        var bytes = token.ToByteArray();
        return await db.RelayOneTimePreKeys.Where(k => k.OwnerRoutingToken == bytes).ToListAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await _server.DisposeAsync().ConfigureAwait(false);
        var stunService = _serviceProvider.GetService<UdpStunResponderService>();
        if (stunService != null)
        {
            await stunService.DisposeAsync().ConfigureAwait(false);
        }
        await _serviceProvider.DisposeAsync().ConfigureAwait(false);
        await _keeperConnection.DisposeAsync().ConfigureAwait(false);

        if (Directory.Exists(_blobStorageDir))
        {
            try
            {
                Directory.Delete(_blobStorageDir, recursive: true);
            }
            catch
            {
                // Best-effort directory cleanup
            }
        }
    }
}
