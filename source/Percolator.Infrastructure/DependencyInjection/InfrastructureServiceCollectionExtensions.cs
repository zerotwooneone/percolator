using Percolator.Application.Delivery;
using Percolator.Application.Delivery.Ports;
using Percolator.Application.Profiles;
using Percolator.Application.Services;
using Percolator.PluginSdk.Profiles;

namespace Percolator.Infrastructure.DependencyInjection;

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Percolator.Application.Ports;
using Percolator.Apps.Chat.Features.GroupInvitations;
using Percolator.Apps.Chat.Features.Messaging;
using Percolator.Apps.Chat.Ingress;
using Percolator.Apps.Discovery.Features.PrivateRendezvous;
using Percolator.Apps.Discovery.Features.RelayDirectoryHosting;
using Percolator.Apps.Discovery.Ports;
using Percolator.Apps.FileTransfer.Features.TransferNegotiation;
using Percolator.Apps.FileTransfer.Ports;
using Percolator.Domain.Channels.Ports;
using Percolator.Domain.Common;
using Percolator.Domain.Delivery.Ports;
using Percolator.Domain.Identities.Ports;
using Percolator.Domain.Security.Ports;
using Percolator.Infrastructure.Adapters;
using Percolator.Infrastructure.Common;
using Percolator.Infrastructure.Events;
using Percolator.Infrastructure.Maintenance;
using Percolator.Infrastructure.Persistence;
using Percolator.Infrastructure.Persistence.Repositories;
using Percolator.Infrastructure.Plugins.BlobStorage;
using Percolator.Infrastructure.Plugins.FileTransfer;
using Percolator.Infrastructure.Security;
using Percolator.Infrastructure.Serialization;
using Percolator.Infrastructure.Transport;
using Percolator.Infrastructure.Transport.Endpoints;
using Percolator.Infrastructure.Transport.Workers;
using Percolator.Infrastructure.Vault;
using Percolator.PluginSdk;

public static class InfrastructureServiceCollectionExtensions
{
    public static IServiceCollection AddBootstrapInfrastructure(
        this IServiceCollection services,
        IConfiguration? configuration = null)
    {
        SQLitePCL.Batteries_V2.Init();

        services.AddSingleton<VaultStorageOptions>();
        services.AddSingleton<IDateTimeProvider, SystemDateTimeProvider>();
        services.AddSingleton<IHeaderStorage, ProtobufHeaderStorage>();
        services.AddSingleton<IDuressWipeHandler, DuressWipeHandler>();
        services.AddSingleton<IVaultEnvelopePacker, VaultEnvelopePacker>();
        services.AddSingleton<IDatabaseCanaryProbe, DatabaseCanaryProbe>();
        // Real schema migrator — opens a keyed DbContext with the just-verified master key
        // before any repositories exist ([DEC-CANARY-ERROR26-UNWRAP]).
        services.AddSingleton<IDatabaseMigrator, SqlCipherSchemaMigrator>();
        // ISessionHostFactory is executable-owned (Desktop registers DesktopStage2SessionFactory);
        // resolving the coordinator without one fails fast at resolution — intended.
        services.AddSingleton<IDatabaseUnlockCoordinator>(sp => new SqlCipherDatabaseUnlockCoordinator(
            sp.GetRequiredService<IHeaderStorage>(),
            sp.GetRequiredService<IVaultEnvelopePacker>(),
            sp.GetRequiredService<IDatabaseCanaryProbe>(),
            sp.GetRequiredService<IDatabaseMigrator>(),
            sp.GetRequiredService<ISessionHostFactory>(),
            sp.GetRequiredService<IDuressWipeHandler>(),
            sp.GetRequiredService<VaultStorageOptions>()));

        return services;
    }

    /// <summary>
    /// Thin aggregator over the session-composition slices — kept so test/simulator hosts
    /// keep a one-line composition. The Desktop executable composes the same slices inside
    /// <see cref="ISessionHostFactory"/> plus its own session registrations.
    /// </summary>
    public static IServiceCollection AddAuthenticatedInfrastructure(
        this IServiceCollection services,
        IMasterKeyHandle masterKeyHandle,
        VaultStorageOptions? options = null,
        IConfiguration? configuration = null)
    {
        ArgumentNullException.ThrowIfNull(masterKeyHandle);
        options ??= new VaultStorageOptions();

        services.AddSessionCoreInfrastructure(masterKeyHandle, options);
        services.AddVaultPersistenceInfrastructure();
        services.AddTransportInfrastructure();
        services.AddSessionLifecycleInfrastructure();
        services.AddChatInfrastructure();
        services.AddDiscoveryInfrastructure();
        services.AddBlobStorageInfrastructure();
        services.AddFileTransferInfrastructure();

        return services;
    }

    /// <summary>
    /// Master key, vault options, connection interceptor, event bus, and platform/crypto
    /// singletons. The only parameterized slice — the rest read these registered
    /// singletons via <c>sp</c>.
    /// </summary>
    public static IServiceCollection AddSessionCoreInfrastructure(
        this IServiceCollection services,
        IMasterKeyHandle masterKeyHandle,
        VaultStorageOptions options)
    {
        ArgumentNullException.ThrowIfNull(masterKeyHandle);
        ArgumentNullException.ThrowIfNull(options);

        SQLitePCL.Batteries_V2.Init();

        services.AddSingleton(masterKeyHandle);
        services.AddSingleton(options);
        services.AddSingleton<DbConnectionInterceptor, SqlCipherPragmaInterceptor>();

        // Event Bus
        services.AddSingleton<InMemoryEventBus>();
        services.AddSingleton<IEventBus>(sp => sp.GetRequiredService<InMemoryEventBus>());
        services.AddSingleton<IEventPublisher>(sp => sp.GetRequiredService<InMemoryEventBus>());
        services.AddSingleton<IEventSubscriber>(sp => sp.GetRequiredService<InMemoryEventBus>());

        // Platform & Cryptography Singletons
        services.AddSingleton<IDateTimeProvider, SystemDateTimeProvider>();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<CryptographyOptions>();
        services.AddSingleton<ICryptoEngine, SignalCryptoEngine>();
        services.AddSingleton<IZkProofEngine, ZkgroupCryptographyService>();
        services.AddSingleton<ISessionWirePacker, ProtobufSessionWirePacker>();
        services.AddSingleton<IPayloadSerializer, ProtobufPayloadSerializer>();
        services.AddSingleton<IProfileMetadataSerializer, ProtobufProfileSerializer>();

        return services;
    }

    /// <summary>
    /// Keyed EF Core context factory, ambient scoped context, migrator, repositories &
    /// query services, vault-backed stores, and the repository-maintenance workers.
    /// Ports ctor-injected into session-long-lived objects are singletons over
    /// <see cref="IDbContextFactory{PercolatorDbContext}"/> (CTX-02 lifetime audit);
    /// handler-only ports stay scoped to keep inbound pipelines transaction-correct.
    /// </summary>
    public static IServiceCollection AddVaultPersistenceInfrastructure(this IServiceCollection services)
    {
        // EF Core SQLCipher DbContext pooling — VaultStorageOptions resolved via sp so the
        // slice is composition-root-agnostic (no captured locals).
        services.AddDbContextFactory<PercolatorDbContext>((sp, dbOptions) =>
        {
            var interceptor = sp.GetRequiredService<DbConnectionInterceptor>();
            var options = sp.GetRequiredService<VaultStorageOptions>();
            dbOptions.UseSqlite($"Data Source={options.DatabaseFilePath};Mode=ReadWriteCreate")
                .AddInterceptors(interceptor);
        });

        // Ambient scoped context — handler/inbound pipelines only; UI paths take the factory.
        services.AddScoped(sp =>
        {
            var factory = sp.GetRequiredService<IDbContextFactory<PercolatorDbContext>>();
            return factory.CreateDbContext();
        });

        services.AddScoped<IDatabaseMigrator, SqliteDatabaseMigrator>();

        // Session-bound facade ports — ctor-injected into session objects, so singletons
        // over the pooled context factory (never an ambient scoped context).
        services.AddSingleton<IIdentityProfileRepository, SqliteIdentityProfileRepository>();
        services.AddSingleton<ILocalIdentityKeyStore, SqliteLocalIdentityKeyStore>();
        services.AddSingleton<IPrivatePreKeyStore, SqlitePrivatePreKeyStore>();
        services.AddSingleton<IRelayRegistrationRepository, SqliteRelayRegistrationRepository>();
        services.AddSingleton<IRelayDirectory, RelayDirectoryService>();
        services.AddSingleton<IRelayMailboxCapabilityStore, SqliteRelayMailboxCapabilityStore>();
        services.AddSingleton<IVaultSettingsStore, SqliteVaultSettingsStore>();

        // Handler-only repositories & CQRS query services — resolved inside scopes.
        services.AddScoped<IRatchetSessionRepository, SqliteRatchetSessionRepository>();
        services.AddScoped<IGroupSenderKeyRepository, SqliteGroupSenderKeyRepository>();
        services.AddScoped<IGroupReceiverSessionRepository, SqliteGroupReceiverSessionRepository>();
        services.AddScoped<IGroupCredentialsRepository, SqliteGroupCredentialsRepository>();
        services.AddScoped<IPendingHandshakeRepository, SqlitePendingHandshakeRepository>();

        services.AddScoped<IOutboxRepository, SqliteOutboxRepository>();
        services.AddScoped<IOutboxQueryService, SqlOutboxQueryService>();

        services.AddScoped<IPeerContactRepository, SqlitePeerContactRepository>();
        services.AddScoped<IPeerContactQueryService, SqlPeerContactQueryService>();
        services.AddScoped<IChannelRepository, SqliteChannelRepository>();

        services.AddScoped(sp => new SqliteRelayPreKeyRepository(sp.GetRequiredService<IDbContextFactory<PercolatorDbContext>>()));
        services.AddScoped<IRelayPreKeyRepository>(sp => sp.GetRequiredService<SqliteRelayPreKeyRepository>());
        services.AddScoped(sp => new SqliteRelayProfileRepository(sp.GetRequiredService<PercolatorDbContext>()));
        services.AddScoped<IRelayProfileRepository>(sp => sp.GetRequiredService<SqliteRelayProfileRepository>());
        services.AddScoped(sp => new SqliteRelayLedgerRepository(sp.GetRequiredService<PercolatorDbContext>()));
        services.AddScoped<IRelayLedgerRepository>(sp => sp.GetRequiredService<SqliteRelayLedgerRepository>());
        services.AddScoped<IPeerProfileRepository, SqlitePeerProfileRepository>();
        services.AddScoped<IPeerMailboxRoutingRepository, SqlitePeerMailboxRoutingRepository>();

        // Repository-maintenance workers
        services.AddHostedService<OutboxRetentionPruner>();
        services.AddHostedService<RelayPreKeyDirectoryPruner>();
        services.AddHostedService<RelayProfileSlotPruner>();

        return services;
    }

    /// <summary>
    /// Transport singletons, the singleton mailbox provisioner, ingress/dispatcher scoped
    /// services, relay endpoints, and the mailbox/group stream workers.
    /// </summary>
    public static IServiceCollection AddTransportInfrastructure(this IServiceCollection services)
    {
        // Transport Singletons
        services.AddSingleton<ITransportCertificateProvider, InMemoryTransportCertificateProvider>();
        services.AddSingleton<PeerGrpcChannelFactoryOptions>();
        services.AddSingleton<IPeerGrpcChannelFactory, PeerGrpcChannelFactory>();
        services.AddSingleton<ITransportClientInvoker, GrpcTransportClientInvoker>();
        services.AddSingleton<StreamRegistryOptions>();
        services.AddSingleton<StreamRegistry>();
        services.AddSingleton<IStreamRegistry>(sp => sp.GetRequiredService<StreamRegistry>());
        services.AddSingleton<IStreamRegistryManagement>(sp => sp.GetRequiredService<StreamRegistry>());
        services.AddSingleton<KestrelPeerTransportServer>();
        services.AddSingleton<IPeerTransportServer>(sp => sp.GetRequiredService<KestrelPeerTransportServer>());
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<KestrelPeerTransportServer>());

        // Session-bound: opens one scope per mailbox drain to resolve the per-node
        // ISealedEnvelopeUnwrapper/IInboundIngressPipeline (CTX-02 lifetime audit).
        services.AddSingleton<IRelayMailboxProvisioner, GrpcRelayMailboxProvisioner>();

        // Transport & ingress scoped services
        services.AddSingleton<IngressFilterOptions>();
        services.AddScoped<IIngressFilterService, IngressFilterService>();
        services.AddScoped<ITransportDispatcher, TransportDispatcher>();
        // ISealedEnvelopeUnwrapper is per-node: its PublicIdentityId recipientId cannot be resolved from DI.
        // Composition roots that know the node identity must register a factory (see SimulatedPersonaHost).
        services.AddScoped<IPortReservationService, SqlitePortReservationService>();
        services.AddScoped<UnauthenticatedDeliveryEndpoint>();
        services.AddScoped<AuthenticatedRelayEndpoint>();
        services.AddScoped<PrivateRendezvousRelayEndpoint>();

        // Mailbox/group stream workers
        services.AddSingleton<RelayMailboxStreamWorkerOptions>();
        services.AddSingleton<RelayGroupStreamWorkerOptions>();
        services.AddHostedService(sp => new RelayMailboxStreamWorker(
            sp.GetService<Func<CancellationToken, Task>>() ?? (async ct => await Task.Delay(Timeout.InfiniteTimeSpan, ct)),
            sp.GetRequiredService<RelayMailboxStreamWorkerOptions>()));
        services.AddHostedService(sp => new RelayGroupStreamWorker(
            sp.GetService<Func<CancellationToken, Task>>() ?? (async ct => await Task.Delay(Timeout.InfiniteTimeSpan, ct)),
            sp.GetRequiredService<RelayGroupStreamWorkerOptions>()));

        return services;
    }

    /// <summary>
    /// Vault-lifecycle primitives needed inside the session container. The Stage-1-only
    /// pieces (ISessionHostFactory, IDatabaseUnlockCoordinator) deliberately stay out —
    /// resolving them in-session would recurse into host composition.
    /// </summary>
    public static IServiceCollection AddSessionLifecycleInfrastructure(this IServiceCollection services)
    {
        services.AddSingleton<IHeaderStorage, ProtobufHeaderStorage>();
        services.AddSingleton<IDuressWipeHandler, DuressWipeHandler>();
        services.AddSingleton<IVaultEnvelopePacker, VaultEnvelopePacker>();
        services.AddSingleton<IDatabaseCanaryProbe, DatabaseCanaryProbe>();

        return services;
    }

    public static IServiceCollection AddAuthenticatedInfrastructure(
        this IServiceCollection services,
        IConfiguration? configuration,
        IMasterKeyHandle masterKeyHandle,
        VaultStorageOptions? options = null)
    {
        return services.AddAuthenticatedInfrastructure(masterKeyHandle, options, configuration);
    }

    public static IServiceCollection AddChatInfrastructure(this IServiceCollection services)
    {
        services.AddScoped<IChatMessageRepository, SqlChatMessageRepository>();
        services.AddScoped<IChatMessageQueryService, SqlChatMessageQueryService>();
        services.AddScoped<IConversationListQueryService, SqlConversationListQueryService>();
        services.AddScoped<IGroupInvitationRepository, SqlGroupInvitationRepository>();
        services.AddScoped<IGroupInvitationQueryService, SqlGroupInvitationQueryService>();
        services.AddScoped<IUnknownGroupMessageCacheRepository, SqlUnknownGroupMessageCacheRepository>();

        services.AddHostedService(sp => new DisappearingMessageEvictionWorker(
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<ILocalChunkStore>(),
            sp.GetRequiredService<TimeProvider>()));
        services.AddHostedService(sp => new UnknownGroupMessageCachePruner(
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<TimeProvider>()));
        services.AddHostedService(sp => new AbandonedInvitePruner(
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<TimeProvider>()));

        return services;
    }

    public static IServiceCollection AddDiscoveryInfrastructure(this IServiceCollection services)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<IDiscoveryAdmissionGate, DiscoveryAdmissionGate>();
        services.AddScoped<IRelayPeerDirectory, SqliteRelayPeerDirectory>();
        services.AddScoped<IPrivateRendezvousService, SqlitePrivateRendezvousService>();
        services.AddScoped<INearbyDiscoveryQueryClientPort, DiscoveryQueryClient>();
        services.AddScoped<IPreKeyAvailabilityPort, PreKeyAvailabilityAdapter>();
        services.AddScoped<IPreKeyBundleProvider, PreKeyBundleProviderAdapter>();
        services.AddScoped<IProfileDeliveryClient, GrpcProfileDeliveryClient>();
        services.AddScoped<IProfileManager, ProfileManager>();

        services.AddHostedService(sp => new RelayDiscoveryRegistrationPruner(
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<TimeProvider>()));

        return services;
    }

    public static IServiceCollection AddBlobStorageInfrastructure(this IServiceCollection services)
    {
        services.AddSingleton<IBlobCryptoService, StreamingAesGcmCryptoService>();
        services.AddSingleton<ILocalChunkStore, LocalFileChunkStore>();
        services.AddScoped<IBlobStorageService, BlobStorageService>();
        services.AddScoped<IRelayBlobClient, RelayBlobClient>();
        services.AddScoped<IPeerStreamBlobClient, PeerStreamBlobClient>();
        services.AddSingleton<IChannelRelayResolver, ChannelRelayResolver>();

        services.AddHostedService<RelayBlobRetentionPruner>();

        return services;
    }

    public static IServiceCollection AddFileTransferInfrastructure(this IServiceCollection services)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IDesktopFileStorage, PortableDesktopFileStore>();
        services.AddSingleton<RelayTransferPipeClient>();
        services.AddSingleton<CallbackEndpointValidatorOptions>();
        services.AddSingleton<CallbackEndpointValidator>();
        services.AddSingleton<ITransferStreamPoolFactory, TcpMultiStreamPoolFactory>();
        services.AddSingleton<ITransferRateLimiter, TokenBucketRateLimiter>();
        services.AddSingleton<INetworkEndpointDiscoveryPort, NetworkInterfaceEndpointDiscoveryPort>();
        services.AddScoped<IDistributedTrackerPort, RelaySwarmTrackerClient>();
        services.AddScoped<ITransferSessionRepository, SqliteTransferSessionRepository>();
        services.AddScoped<IFileTransferFilterPolicyRepository, SqliteFilterPolicyRepository>();
        services.AddScoped<IFileTransferQueryService, SqlFileTransferQueryService>();
        services.AddScoped<ITransferOfferReaperService, TransferOfferReaperService>();

        services.AddHostedService<TransferOfferReaperWorker>();
        services.AddHostedService<SwarmEndpointRetentionPruner>();

        return services;
    }
}
