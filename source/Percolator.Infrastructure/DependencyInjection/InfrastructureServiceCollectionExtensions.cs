using Percolator.Application.Delivery;
using Percolator.Application.Delivery.Ports;
using Percolator.Application.Egress;
using Percolator.Application.Handshake;
using Percolator.Application.Hosting;
using Percolator.Application.Ingress;
using Percolator.Application.Invitations;
using Percolator.Application.Profiles;
using Percolator.Application.Routing;
using Percolator.Application.Services;
using Percolator.Application.Session;
using Percolator.Application.Session.Ports;
using Percolator.Domain.Delivery.Hosting;
using Percolator.PluginSdk.Profiles;

namespace Percolator.Infrastructure.DependencyInjection;

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Percolator.Application.Ports;
using Percolator.PluginSdk.Events;
using Percolator.Apps.Chat.Features.GroupInvitations;
using Percolator.Apps.Chat.Features.Introductions;
using Percolator.Apps.Chat.Features.Messaging;

using Percolator.Apps.Chat.Ingress;
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
using Percolator.Infrastructure.Persistence.Queries;
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

        services.TryAddSingleton<PortReservationService>();
        services.TryAddSingleton<IPortReservationService>(sp => sp.GetRequiredService<PortReservationService>());
        services.TryAddSingleton<ISimulatedPortReservationService>(sp => sp.GetRequiredService<PortReservationService>());
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

        // Active-persona registry — seeded by the composition root before StartAsync so
        // hosted workers observe a populated set on their first reconciliation.
        services.AddSingleton<IActiveIdentitySet, ActiveIdentitySet>();

        // Platform & Cryptography Singletons
        services.AddSingleton<IDateTimeProvider, SystemDateTimeProvider>();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<CryptographyOptions>();
        services.AddSingleton<ICryptoEngine, SignalCryptoEngine>();
        services.AddSingleton<IZkProofEngine, ZkgroupCryptographyService>();
        services.AddSingleton<ISessionWirePacker, ProtobufSessionWirePacker>();
        services.AddSingleton<IPayloadSerializer, ProtobufPayloadSerializer>();
        services.AddSingleton<IProfileMetadataSerializer, ProtobufProfileSerializer>();
        services.AddSingleton<IPeerInvitationCodec, PeerInvitationCodec>();

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
        // Mailbox persist-then-ack spool (DRAIN-05) — ambient scoped context like the
        // other handler-only repositories; resolved inside drain scopes.
        services.AddScoped<IPendingIngressStore, SqlitePendingIngressStore>();

        services.AddScoped<IOutboxRepository, SqliteOutboxRepository>();
        services.AddScoped<IOutboxQueryService, SqlOutboxQueryService>();

        services.AddScoped<IPeerContactRepository, SqlitePeerContactRepository>();
        services.AddSingleton<IPeerContactQueryService, SqlPeerContactQueryService>();
        services.AddSingleton<IPersonaRelayQueryService, SqlPersonaRelayQueryService>();
        services.AddScoped<IChannelRepository, SqliteChannelRepository>();

        services.AddScoped(sp => new SqliteRelayPreKeyRepository(sp.GetRequiredService<IDbContextFactory<PercolatorDbContext>>()));
        services.AddScoped<IRelayPreKeyRepository>(sp => sp.GetRequiredService<SqliteRelayPreKeyRepository>());
        services.AddScoped(sp => new SqliteRelayProfileRepository(sp.GetRequiredService<PercolatorDbContext>()));
        services.AddScoped<IRelayProfileRepository>(sp => sp.GetRequiredService<SqliteRelayProfileRepository>());
        services.AddScoped(sp => new SqliteRelayLedgerRepository(sp.GetRequiredService<PercolatorDbContext>()));
        services.AddScoped<IRelayLedgerRepository>(sp => sp.GetRequiredService<SqliteRelayLedgerRepository>());
        services.AddScoped<IPeerProfileRepository, SqlitePeerProfileRepository>();
        services.AddScoped<IPeerMailboxRoutingRepository, SqlitePeerMailboxRoutingRepository>();
        services.AddScoped<ILocalRelayAssociationRepository, SqliteLocalRelayAssociationRepository>();
        services.AddScoped<IPendingMailboxRegistrationRepository, SqlitePendingMailboxRegistrationRepository>();
        services.AddScoped<IExchangeRegistrationModerationService, Percolator.Application.Hosting.ExchangeRegistrationModerationService>();

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
        services.TryAddSingleton<PortReservationService>();
        services.TryAddSingleton<IPortReservationService>(sp => sp.GetRequiredService<PortReservationService>());
        services.TryAddSingleton<ISimulatedPortReservationService>(sp => sp.GetRequiredService<PortReservationService>());
        services.AddSingleton<ITransportCertificateProvider, InMemoryTransportCertificateProvider>();
        services.AddSingleton<PeerGrpcChannelFactoryOptions>();
        services.AddSingleton<IPeerGrpcChannelFactory, PeerGrpcChannelFactory>();
        services.AddSingleton<ITransportClientInvoker, GrpcTransportClientInvoker>();
        services.AddSingleton<IRelayHostingPolicyProvider, Percolator.Domain.Delivery.Hosting.RelayHostingPolicyHolder>();
        services.AddSingleton<IAuthenticatedMemberRateLimiter, InMemoryAuthenticatedMemberRateLimiter>();
        services.AddSingleton<PeerTransportServerOptions>();
        services.AddSingleton<KestrelPeerTransportServer>();
        services.AddSingleton<IPeerTransportServer>(sp => sp.GetRequiredService<KestrelPeerTransportServer>());
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<KestrelPeerTransportServer>());

        // Session-bound: opens one scope per mailbox drain to resolve the per-node
        // ISealedEnvelopeUnwrapper/IInboundIngressPipeline (CTX-02 lifetime audit).
        services.AddSingleton<IRelayMailboxProvisioner, GrpcRelayMailboxProvisioner>();
        services.AddSingleton<IRelayMailboxClient, GrpcRelayMailboxClient>();

        // Transport & ingress scoped services
        services.AddSingleton<IngressFilterOptions>();
        services.AddScoped<IIngressFilterService, IngressFilterService>();
        services.AddScoped<ITransportDispatcher, TransportDispatcher>();
        // ISealedEnvelopeUnwrapper is recipient-parameterized since DRAIN-04 — shared by
        // every persona; callers pass the recipient PublicIdentityId per call.
        services.AddScoped<ISealedEnvelopeUnwrapper, SealedEnvelopeUnwrapper>();

        // Inbound ingress chain + mailbox drain (DRAIN-05). Sim hosts re-register the
        // same services after this slice with their decorators — last registration wins,
        // so per-host overrides still apply. IAppPayloadHandler/IAppIngressFilter are
        // contributed by the vertical app registrations (AddChatApp/AddFileTransferApp).
        services.AddSingleton<IChannelLockService, KeyedSemaphoreLockService>();
        services.AddSingleton<IHandshakeReplayFilter, MemoryHandshakeReplayFilter>();
        services.AddSingleton<IPreKeyMaintenanceService, PreKeyMaintenanceService>();
        services.AddScoped<IContactRequestCoordinator, ContactRequestCoordinator>();
        services.AddScoped<IRoutingCoordinator, RoutingCoordinator>();
        services.AddScoped<IPayloadSender, OutboundEgressPipeline>();
        services.AddScoped<IGroupKeyDistributionService, GroupKeyDistributionService>();
        services.AddScoped<IPeerInvitationService, PeerInvitationService>();
        services.AddScoped<AppRouter>();
        services.AddScoped<IAppRouter>(sp =>
        {
            var router = sp.GetRequiredService<AppRouter>();
            foreach (var handler in sp.GetServices<IAppPayloadHandler>())
            {
                router.RegisterHandler(handler);
            }
            foreach (var filter in sp.GetServices<IAppIngressFilter>())
            {
                router.RegisterFilter(filter);
            }
            return router;
        });
        services.AddScoped<IHandshakeService, HandshakeService>();
        services.AddScoped<IInboundIngressPipeline, InboundIngressPipeline>();
        services.AddScoped<OutboxWorker>();
        services.AddScoped<MailboxDrainService>();

        services.AddScoped<IPreKeyBundleProvider, PreKeyBundleProviderAdapter>();
        services.AddScoped<IProfileDeliveryClient, GrpcProfileDeliveryClient>();
        services.AddScoped<IProfileManager, ProfileManager>();

        services.AddScoped<UnauthenticatedDeliveryEndpoint>();
        services.AddScoped<AuthenticatedRelayEndpoint>();
        services.AddSingleton<InMemorySwarmTrackerRegistry>();
        services.AddSingleton<InMemoryTransferPipeBroker>();
        services.AddSingleton<UdpStunResponderOptions>();
        services.AddSingleton<UdpStunResponderService>();
        services.AddSingleton<IUdpStunEndpointProvider>(sp => sp.GetRequiredService<UdpStunResponderService>());
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<UdpStunResponderService>());
        services.AddScoped<ExchangeSwarmTrackerEndpoint>();
        services.AddScoped<ExchangeTransferPipeEndpoint>();

        // Mailbox/group stream workers
        services.AddSingleton<DrainSchedulerOptions>();
        services.AddSingleton<DrainScheduler>();
        services.AddSingleton<RelayGroupStreamWorkerOptions>();
        services.AddHostedService<RelayMailboxDrainHost>();
        services.AddHostedService(sp => new RelayGroupStreamWorker(
            sp.GetService<Func<CancellationToken, Task>>() ?? (async ct => await Task.Delay(Timeout.InfiniteTimeSpan, ct)),
            sp.GetRequiredService<RelayGroupStreamWorkerOptions>()));

        // Outbox reactive pump
        services.AddSingleton<OutboxDispatchHostOptions>();
        services.AddHostedService<OutboxDispatchHost>();

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
        services.AddSingleton<IChatMessageQueryService, SqlChatMessageQueryService>();
        services.AddSingleton<IConversationListQueryService, SqlConversationListQueryService>();
        services.AddScoped<IGroupInvitationRepository, SqlGroupInvitationRepository>();
        services.AddScoped<IGroupInvitationQueryService, SqlGroupInvitationQueryService>();
        services.AddScoped<IPeerIntroductionConsentRepository, SqlPeerIntroductionConsentRepository>();
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

    public static IServiceCollection AddBlobStorageInfrastructure(this IServiceCollection services)
    {
        services.AddSingleton<IBlobCryptoService, StreamingAesGcmCryptoService>();
        services.AddSingleton<ILocalChunkStore, LocalFileChunkStore>();
        services.AddScoped<IBlobStorageService, BlobStorageService>();
        services.AddScoped<IRelayBlobClient, RelayBlobClient>();
        services.AddSingleton<IChannelRelayResolver, ChannelRelayResolver>();

        services.AddHostedService<RelayBlobRetentionPruner>();

        return services;
    }

    public static IServiceCollection AddFileTransferInfrastructure(this IServiceCollection services)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IDesktopFileStorage, PortableDesktopFileStore>();
        services.AddSingleton(new CallbackEndpointValidatorOptions { AllowLan = true, AllowLoopback = true });
        services.AddSingleton<CallbackEndpointValidator>();
        services.AddSingleton<ITransferRateLimiter, TokenBucketRateLimiter>();
        services.AddSingleton<ITransferSessionCertificateStore, SqliteTransferSessionCertificateStore>();
        services.AddSingleton<ITransferSessionTransportStore, SqliteTransferSessionTransportStore>();
        services.AddSingleton<ITransferPeerTransport, TransferPeerTransport>();
        services.AddSingleton<Percolator.Apps.FileTransfer.Ports.INetworkEndpointDiscoveryPort, NetworkInterfaceEndpointDiscoveryPort>();
        services.AddSingleton<IDistributedTrackerPort, GrpcSwarmTrackerClient>();
        services.AddSingleton<ITransferSessionRepository, SqliteTransferSessionRepository>();
        services.AddSingleton<IFileTransferFilterPolicyRepository, SqliteFilterPolicyRepository>();
        services.AddSingleton<IFileTransferQueryService, SqlFileTransferQueryService>();

        services.AddHostedService<TransferOfferReaperWorker>();

        return services;
    }
}
