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
using Percolator.Application2.Profiles;
using Percolator.Application2.Services;
using Percolator.Application2.Delivery.Ports;
using Percolator.Application2.Ports;
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
using Percolator.Infrastructure2.Serialization;
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
        services.AddSingleton<IDatabaseMigrator, NoOpDatabaseMigrator>();
        services.AddSingleton<IStage2HostFactory, DefaultStage2HostFactory>();
        services.AddSingleton<IDatabaseUnlockCoordinator>(sp => new SqlCipherDatabaseUnlockCoordinator(
            sp.GetRequiredService<IHeaderStorage>(),
            sp.GetRequiredService<IVaultEnvelopePacker>(),
            sp.GetRequiredService<IDatabaseCanaryProbe>(),
            sp.GetRequiredService<IDatabaseMigrator>(),
            sp.GetRequiredService<IStage2HostFactory>(),
            sp.GetRequiredService<IDuressWipeHandler>(),
            sp.GetService<VaultStorageOptions>()));

        return services;
    }

    public static IServiceCollection AddAuthenticatedInfrastructure(
        this IServiceCollection services,
        IMasterKeyHandle masterKeyHandle,
        VaultStorageOptions? options = null,
        IConfiguration? configuration = null)
    {
        ArgumentNullException.ThrowIfNull(masterKeyHandle);
        options ??= new VaultStorageOptions();

        SQLitePCL.Batteries_V2.Init();

        // 1. Core Singletons
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
        services.AddSingleton<ICryptoEngine, SignalCryptoEngine>();
        services.AddSingleton<IZkProofEngine, ZkgroupCryptographyService>();
        services.AddSingleton<ISessionWirePacker, ProtobufSessionWirePacker>();
        services.AddSingleton<IPayloadSerializer, ProtobufPayloadSerializer>();
        services.AddSingleton<IProfileMetadataSerializer, ProtobufProfileSerializer>();

        // Transport Singletons
        services.AddSingleton<ITransportCertificateProvider, InMemoryTransportCertificateProvider>();
        services.AddSingleton<IPeerGrpcChannelFactory, PeerGrpcChannelFactory>();
        services.AddSingleton<ITransportClientInvoker, GrpcTransportClientInvoker>();
        services.AddSingleton<StreamRegistry>();
        services.AddSingleton<IStreamRegistry>(sp => sp.GetRequiredService<StreamRegistry>());
        services.AddSingleton<IStreamRegistryManagement>(sp => sp.GetRequiredService<StreamRegistry>());
        services.AddSingleton<KestrelPeerTransportServer>();
        services.AddSingleton<IPeerTransportServer>(sp => sp.GetRequiredService<KestrelPeerTransportServer>());
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<KestrelPeerTransportServer>());

        // Storage & Transfer Singletons
        services.AddSingleton<IBlobCryptoService, StreamingAesGcmCryptoService>();
        services.AddSingleton<ILocalChunkStore, LocalFileChunkStore>();
        services.AddSingleton<IDesktopFileStorage, PortableDesktopFileStore>();
        services.AddSingleton<RelayTransferPipeClient>();
        services.AddSingleton<ITransferStreamPoolFactory, TcpMultiStreamPoolFactory>();
        services.AddSingleton<ITransferRateLimiter, TokenBucketRateLimiter>();
        services.AddSingleton<INetworkEndpointDiscoveryPort, NetworkInterfaceEndpointDiscoveryPort>();

        // Vault Lifecycle Singletons
        services.AddSingleton<IHeaderStorage, ProtobufHeaderStorage>();
        services.AddSingleton<IDuressWipeHandler, DuressWipeHandler>();
        services.AddSingleton<IVaultEnvelopePacker, VaultEnvelopePacker>();
        services.AddSingleton<IDatabaseCanaryProbe, DatabaseCanaryProbe>();
        services.AddSingleton<IStage2HostFactory, Stage2HostFactory>();
        services.AddScoped<IDatabaseUnlockCoordinator>(sp => new SqlCipherDatabaseUnlockCoordinator(
            sp.GetRequiredService<IHeaderStorage>(),
            sp.GetRequiredService<IVaultEnvelopePacker>(),
            sp.GetRequiredService<IDatabaseCanaryProbe>(),
            sp.GetRequiredService<IDatabaseMigrator>(),
            sp.GetRequiredService<IStage2HostFactory>(),
            sp.GetRequiredService<IDuressWipeHandler>(),
            sp.GetService<VaultStorageOptions>()));
        services.AddSingleton<AuthenticatedSessionHolder>();
        services.AddSingleton<IAuthenticatedSession>(sp =>
        {
            var holder = sp.GetRequiredService<AuthenticatedSessionHolder>();
            return holder.Session ?? throw new InvalidOperationException("Authenticated session has not yet been bound to the host.");
        });

        // 2. EF Core SQLCipher DbContext Pooling
        services.AddDbContextFactory<PercolatorDbContext>((sp, dbOptions) =>
        {
            var interceptor = sp.GetRequiredService<DbConnectionInterceptor>();
            dbOptions.UseSqlite($"Data Source={options.DatabaseFilePath};Mode=ReadWriteCreate")
                .AddInterceptors(interceptor);
        });

        services.AddScoped(sp =>
        {
            var factory = sp.GetRequiredService<IDbContextFactory<PercolatorDbContext>>();
            return factory.CreateDbContext();
        });

        services.AddScoped<IDatabaseMigrator, SqliteDatabaseMigrator>();

        // 3. Repositories & CQRS Query Services
        services.AddScoped<IIdentityProfileRepository, SqliteIdentityProfileRepository>();
        services.AddScoped<ILocalIdentityKeyStore, SqliteLocalIdentityKeyStore>();
        services.AddScoped<IPrivatePreKeyStore, SqlitePrivatePreKeyStore>();
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

        services.AddScoped(sp => new SqliteRelayPreKeyRepository(sp.GetRequiredService<PercolatorDbContext>()));
        services.AddScoped<IRelayPreKeyRepository>(sp => sp.GetRequiredService<SqliteRelayPreKeyRepository>());
        services.AddScoped(sp => new SqliteRelayProfileRepository(sp.GetRequiredService<PercolatorDbContext>()));
        services.AddScoped<IRelayProfileRepository>(sp => sp.GetRequiredService<SqliteRelayProfileRepository>());
        services.AddScoped(sp => new SqliteRelayLedgerRepository(sp.GetRequiredService<PercolatorDbContext>()));
        services.AddScoped<IRelayLedgerRepository>(sp => sp.GetRequiredService<SqliteRelayLedgerRepository>());
        services.AddScoped<IPeerProfileRepository, SqlitePeerProfileRepository>();
        services.AddScoped<IPeerMailboxRoutingRepository, SqlitePeerMailboxRoutingRepository>();

        // 4. Transport & Ingress Scoped Services
        services.AddScoped<IIngressFilterService, IngressFilterService>();
        services.AddScoped<ITransportDispatcher, TransportDispatcher>();
        services.AddScoped<ISealedEnvelopeUnwrapper, SealedEnvelopeUnwrapper>();
        services.AddScoped<IPortReservationService, SqlitePortReservationService>();
        services.AddScoped<UnauthenticatedDeliveryEndpoint>();

        // 5. Vertical Plugins
        // Chat
        services.AddScoped<IChatMessageRepository, SqlChatMessageRepository>();
        services.AddScoped<IChatMessageQueryService, SqlChatMessageQueryService>();
        services.AddScoped<IConversationListQueryService, SqlConversationListQueryService>();
        services.AddScoped<IGroupInvitationRepository, SqlGroupInvitationRepository>();
        services.AddScoped<IGroupInvitationQueryService, SqlGroupInvitationQueryService>();
        services.AddScoped<IUnknownGroupMessageCacheRepository, SqlUnknownGroupMessageCacheRepository>();

        // Discovery
        services.AddScoped<IDiscoveryAdmissionGate, DiscoveryAdmissionGate>();
        services.AddScoped<IRelayPeerDirectory, SqliteRelayPeerDirectory>();
        services.AddScoped<IPrivateRendezvousService, SqlitePrivateRendezvousService>();
        services.AddScoped<INearbyDiscoveryQueryClientPort, DiscoveryQueryClient>();
        services.AddScoped<IPreKeyAvailabilityPort, PreKeyAvailabilityAdapter>();
        services.AddScoped<IPreKeyBundleProvider, PreKeyBundleProviderAdapter>();
        services.AddScoped<IProfileDeliveryClient, GrpcProfileDeliveryClient>();
        services.AddScoped<IProfileManager, ProfileManager>();

        // Blob Storage
        services.AddScoped<IBlobStorageService, BlobStorageService>();
        services.AddScoped<IRelayBlobClient, RelayBlobClient>();
        services.AddScoped<IPeerStreamBlobClient, PeerStreamBlobClient>();
        services.AddScoped<IChannelRelayResolver, ChannelRelayResolver>();

        // File Transfer
        services.AddScoped<IDistributedTrackerPort, RelaySwarmTrackerClient>();
        services.AddScoped<ITransferSessionRepository, SqliteTransferSessionRepository>();
        services.AddScoped<IFileTransferFilterPolicyRepository, SqliteFilterPolicyRepository>();
        services.AddScoped<IFileTransferQueryService, SqlFileTransferQueryService>();
        services.AddScoped<ITransferOfferReaperService, TransferOfferReaperService>();

        // 6. Background Hosted Services (All 12)
        services.AddHostedService<OutboxRetentionPruner>();
        services.AddHostedService<RelayPreKeyDirectoryPruner>();
        services.AddHostedService<RelayProfileSlotPruner>();
        services.AddHostedService(sp => new DisappearingMessageEvictionWorker(
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetService<TimeProvider>(),
            sp.GetService<ILocalChunkStore>()));
        services.AddHostedService(sp => new UnknownGroupMessageCachePruner(
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetService<TimeProvider>()));
        services.AddHostedService(sp => new AbandonedInvitePruner(
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetService<TimeProvider>()));
        services.AddHostedService(sp => new RelayDiscoveryRegistrationPruner(
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetService<TimeProvider>()));
        services.AddHostedService<RelayBlobRetentionPruner>();
        services.AddHostedService<TransferOfferReaperWorker>();
        services.AddHostedService<SwarmEndpointRetentionPruner>();
        services.AddHostedService(sp => new RelayMailboxStreamWorker(
            sp.GetService<Func<CancellationToken, Task>>() ?? (async ct => await Task.Delay(Timeout.InfiniteTimeSpan, ct)),
            sp.GetService<RelayMailboxStreamWorkerOptions>()));
        services.AddHostedService(sp => new RelayGroupStreamWorker(
            sp.GetService<Func<CancellationToken, Task>>() ?? (async ct => await Task.Delay(Timeout.InfiniteTimeSpan, ct)),
            sp.GetService<RelayGroupStreamWorkerOptions>()));

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
            sp.GetService<TimeProvider>(),
            sp.GetService<ILocalChunkStore>()));
        services.AddHostedService(sp => new UnknownGroupMessageCachePruner(
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetService<TimeProvider>()));
        services.AddHostedService(sp => new AbandonedInvitePruner(
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetService<TimeProvider>()));

        return services;
    }

    public static IServiceCollection AddDiscoveryInfrastructure(this IServiceCollection services)
    {
        services.AddScoped<IDiscoveryAdmissionGate, DiscoveryAdmissionGate>();
        services.AddScoped<IRelayPeerDirectory, SqliteRelayPeerDirectory>();
        services.AddScoped<IPrivateRendezvousService, SqlitePrivateRendezvousService>();
        services.AddScoped<INearbyDiscoveryQueryClientPort, DiscoveryQueryClient>();
        services.AddScoped<IPreKeyAvailabilityPort, PreKeyAvailabilityAdapter>();
        services.AddScoped<IPreKeyBundleProvider, PreKeyBundleProviderAdapter>();
        services.AddScoped<IProfileDeliveryClient, GrpcProfileDeliveryClient>();

        services.AddHostedService(sp => new RelayDiscoveryRegistrationPruner(
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetService<TimeProvider>()));

        return services;
    }

    public static IServiceCollection AddBlobStorageInfrastructure(this IServiceCollection services)
    {
        services.AddSingleton<IBlobCryptoService, StreamingAesGcmCryptoService>();
        services.AddSingleton<ILocalChunkStore, LocalFileChunkStore>();
        services.AddScoped<IRelayBlobClient, RelayBlobClient>();
        services.AddScoped<IPeerStreamBlobClient, PeerStreamBlobClient>();
        services.AddScoped<IChannelRelayResolver, ChannelRelayResolver>();

        services.AddHostedService<RelayBlobRetentionPruner>();

        return services;
    }

    public static IServiceCollection AddFileTransferInfrastructure(this IServiceCollection services)
    {
        services.AddSingleton<IDesktopFileStorage, PortableDesktopFileStore>();
        services.AddSingleton<RelayTransferPipeClient>();
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
