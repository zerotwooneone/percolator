namespace Percolator.Infrastructure.Tests.Composition;

using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using Percolator.Application.Ports;
using Percolator.Application2.Delivery.Ports;
using Percolator.Application2.Ports;
using Percolator.Apps.Chat.Features.GroupInvitations;
using Percolator.Apps.Chat.Features.Messaging;
using Percolator.Apps.Chat.Ingress;
using Percolator.Apps.Discovery.Features.PrivateRendezvous;
using Percolator.Apps.Discovery.Features.RelayDirectoryHosting;
using Percolator.Apps.Discovery.Ports;
using Percolator.Apps.FileTransfer.Ports;
using Percolator.Domain.Channels.Ports;
using Percolator.Domain.Common;
using Percolator.Domain.Delivery.Ports;
using Percolator.Domain.Identities.Ports;
using Percolator.Domain.Security.Ports;
using Percolator.Infrastructure.Events;
using Percolator.Infrastructure.Maintenance;
using Percolator.Infrastructure.Persistence;
using Percolator.Infrastructure.Plugins.BlobStorage;
using Percolator.Infrastructure.Plugins.FileTransfer;
using Percolator.Infrastructure.Transport;
using Percolator.Infrastructure.Transport.Workers;
using Percolator.Infrastructure.Vault;
using Percolator.PluginSdk;

[TestFixture]
public sealed class DependencyInjectionTests
{
    private string _tempDir = null!;
    private MasterKeyHandle _keyHandle = null!;

    [SetUp]
    public void SetUp()
    {
        SQLitePCL.Batteries_V2.Init();
        _tempDir = Path.Combine(Path.GetTempPath(), "stage2_di_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _keyHandle = MasterKeyHandle.GenerateRandom();
    }

    [TearDown]
    public void TearDown()
    {
        _keyHandle.Dispose();
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, true); } catch { }
        }
    }

    [Test]
    public void All61Ports_ResolveFromContainerWithoutCycles()
    {
        // Arrange
        var options = new VaultStorageOptions { BaseDirectory = _tempDir };
        var factory = new Stage2HostFactory(options);

        // Act
        using var host = factory.BuildAuthenticatedHost(_keyHandle);
        using var scope = host.Services.CreateScope();
        var sp = scope.ServiceProvider;

        // Assert: 61 Ports
        // 1. Core Cryptography & Platform
        sp.GetRequiredService<ICryptoEngine>().Should().NotBeNull();
        sp.GetRequiredService<IZkProofEngine>().Should().NotBeNull();
        sp.GetRequiredService<IDateTimeProvider>().Should().NotBeNull();

        // 2. Vault Lifecycle
        sp.GetRequiredService<IDatabaseUnlockCoordinator>().Should().NotBeNull();
        sp.GetRequiredService<IAuthenticatedSession>().Should().NotBeNull();
        sp.GetRequiredService<IMasterKeyHandle>().Should().NotBeNull();
        sp.GetRequiredService<IDuressWipeHandler>().Should().NotBeNull();
        sp.GetRequiredService<IHeaderStorage>().Should().NotBeNull();

        // 3. Serialization & DB Interceptor
        sp.GetRequiredService<ISessionWirePacker>().Should().NotBeNull();
        sp.GetRequiredService<IPayloadSerializer>().Should().NotBeNull();
        sp.GetRequiredService<DbConnectionInterceptor>().Should().NotBeNull();

        // 4. Persistence Repositories & Outbox
        sp.GetRequiredService<IOutboxRepository>().Should().NotBeNull();
        sp.GetRequiredService<IPeerContactRepository>().Should().NotBeNull();
        sp.GetRequiredService<IRatchetSessionRepository>().Should().NotBeNull();
        sp.GetRequiredService<IGroupReceiverSessionRepository>().Should().NotBeNull();
        sp.GetRequiredService<IGroupSenderKeyRepository>().Should().NotBeNull();
        sp.GetRequiredService<IPendingHandshakeRepository>().Should().NotBeNull();
        sp.GetRequiredService<IPrivatePreKeyStore>().Should().NotBeNull();
        sp.GetRequiredService<ILocalIdentityKeyStore>().Should().NotBeNull();
        sp.GetRequiredService<IGroupCredentialsRepository>().Should().NotBeNull();
        sp.GetRequiredService<IChannelRepository>().Should().NotBeNull();
        sp.GetRequiredService<IRelayPreKeyRepository>().Should().NotBeNull();
        sp.GetRequiredService<IRelayProfileRepository>().Should().NotBeNull();
        sp.GetRequiredService<IPeerProfileRepository>().Should().NotBeNull();
        sp.GetRequiredService<IPeerMailboxRoutingRepository>().Should().NotBeNull();
        sp.GetRequiredService<IRelayLedgerRepository>().Should().NotBeNull();

        // 5. Vertical Plugins Repositories
        sp.GetRequiredService<IChatMessageRepository>().Should().NotBeNull();
        sp.GetRequiredService<IGroupInvitationRepository>().Should().NotBeNull();
        sp.GetRequiredService<IUnknownGroupMessageCacheRepository>().Should().NotBeNull();
        sp.GetRequiredService<ITransferSessionRepository>().Should().NotBeNull();
        sp.GetRequiredService<IFileTransferFilterPolicyRepository>().Should().NotBeNull();
        sp.GetRequiredService<IRelayPeerDirectory>().Should().NotBeNull();

        // 6. CQRS Query Services
        sp.GetRequiredService<IChatMessageQueryService>().Should().NotBeNull();
        sp.GetRequiredService<IConversationListQueryService>().Should().NotBeNull();
        sp.GetRequiredService<IGroupInvitationQueryService>().Should().NotBeNull();
        sp.GetRequiredService<IOutboxQueryService>().Should().NotBeNull();
        sp.GetRequiredService<IPeerContactQueryService>().Should().NotBeNull();
        sp.GetRequiredService<IFileTransferQueryService>().Should().NotBeNull();

        // 7. Transport & Routing
        sp.GetRequiredService<IIngressFilterService>().Should().NotBeNull();
        sp.GetRequiredService<IStreamRegistry>().Should().NotBeNull();
        sp.GetRequiredService<ITransportDispatcher>().Should().NotBeNull();
        // ISealedEnvelopeUnwrapper is per-node (requires the node's PublicIdentityId) and is
        // intentionally not DI-registered; composition roots register a factory.

        // 8. Discovery & Profile Delivery
        sp.GetRequiredService<INearbyDiscoveryQueryClientPort>().Should().NotBeNull();
        sp.GetRequiredService<IPreKeyAvailabilityPort>().Should().NotBeNull();
        sp.GetRequiredService<IPreKeyBundleProvider>().Should().NotBeNull();
        sp.GetRequiredService<IProfileDeliveryClient>().Should().NotBeNull();
        sp.GetRequiredService<IProfileManager>().Should().NotBeNull();

        // 9. Blob Storage
        sp.GetRequiredService<IBlobCryptoService>().Should().NotBeNull();
        sp.GetRequiredService<IRelayBlobClient>().Should().NotBeNull();
        sp.GetRequiredService<IPeerStreamBlobClient>().Should().NotBeNull();
        sp.GetRequiredService<ILocalChunkStore>().Should().NotBeNull();
        sp.GetRequiredService<IChannelRelayResolver>().Should().NotBeNull();
        sp.GetRequiredService<IBlobStorageService>().Should().NotBeNull();

        // 10. File Transfer & Swarm
        sp.GetRequiredService<ITransferStreamPoolFactory>().Should().NotBeNull();
        sp.GetRequiredService<INetworkEndpointDiscoveryPort>().Should().NotBeNull();
        sp.GetRequiredService<IDesktopFileStorage>().Should().NotBeNull();
        sp.GetRequiredService<ITransferRateLimiter>().Should().NotBeNull();
        sp.GetRequiredService<IDistributedTrackerPort>().Should().NotBeNull();

        // 11. Remaining Ports & Serializers
        sp.GetRequiredService<ITransportCertificateProvider>().Should().NotBeNull();
        sp.GetRequiredService<IIdentityProfileRepository>().Should().NotBeNull();
        sp.GetRequiredService<IPortReservationService>().Should().NotBeNull();
        sp.GetRequiredService<IPrivateRendezvousService>().Should().NotBeNull();
        sp.GetRequiredService<IProfileMetadataSerializer>().Should().NotBeNull();
        sp.GetRequiredService<IStage2HostFactory>().Should().NotBeNull();
    }

    [Test]
    public void All12HostedServices_RegisteredAndResolvable()
    {
        // Arrange
        var options = new VaultStorageOptions { BaseDirectory = _tempDir };
        var factory = new Stage2HostFactory(options);

        // Act
        using var host = factory.BuildAuthenticatedHost(_keyHandle);
        var hostedServices = host.Services.GetServices<IHostedService>().ToList();

        // Assert: Resilient capability containment instead of brittle magic-number count assertions
        var serviceTypes = hostedServices.Select(s => s.GetType()).ToList();
        serviceTypes.Should().Contain([
            typeof(OutboxRetentionPruner),
            typeof(RelayPreKeyDirectoryPruner),
            typeof(RelayProfileSlotPruner),
            typeof(DisappearingMessageEvictionWorker),
            typeof(UnknownGroupMessageCachePruner),
            typeof(AbandonedInvitePruner),
            typeof(RelayDiscoveryRegistrationPruner),
            typeof(RelayBlobRetentionPruner),
            typeof(TransferOfferReaperWorker),
            typeof(SwarmEndpointRetentionPruner),
            typeof(RelayMailboxStreamWorker),
            typeof(RelayGroupStreamWorker)
        ]);
    }

    [Test]
    public async Task AuthenticatedHost_HostedServices_StartAndStopGracefully()
    {
        // Arrange
        var options = new VaultStorageOptions { BaseDirectory = _tempDir };
        var factory = new Stage2HostFactory(options);

        // Act & Assert
        using var host = factory.BuildAuthenticatedHost(_keyHandle);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var startAction = async () => await host.StartAsync(cts.Token);
        await startAction.Should().NotThrowAsync("All background hosted services must start cleanly without throwing or CPU spinning");

        var stopAction = async () => await host.StopAsync(cts.Token);
        await stopAction.Should().NotThrowAsync("All background hosted services must stop cleanly upon cancellation");
    }

    [Test]
    public async Task AuthenticatedSession_ResolvedFromContainer_ControlsRealHostLifecycle()
    {
        // Arrange
        var options = new VaultStorageOptions { BaseDirectory = _tempDir };
        var factory = new Stage2HostFactory(options);

        // Act
        using var host = factory.BuildAuthenticatedHost(_keyHandle);
        var session = host.Services.GetRequiredService<IAuthenticatedSession>();

        // Assert
        session.Should().NotBeNull();
        session.Services.Should().BeSameAs(host.Services);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var startAction = async () => await session.StartAsync(cts.Token);
        await startAction.Should().NotThrowAsync();

        var stopAction = async () => await session.StopAsync(cts.Token);
        await stopAction.Should().NotThrowAsync();
    }

    [Test]
    public void ChannelRelayResolver_IsSingletonAcrossScopes()
    {
        // Arrange
        var options = new VaultStorageOptions { BaseDirectory = _tempDir };
        var factory = new Stage2HostFactory(options);

        // Act
        using var host = factory.BuildAuthenticatedHost(_keyHandle);
        using var scope1 = host.Services.CreateScope();
        using var scope2 = host.Services.CreateScope();

        var resolver1 = scope1.ServiceProvider.GetRequiredService<IChannelRelayResolver>();
        var resolver2 = scope2.ServiceProvider.GetRequiredService<IChannelRelayResolver>();

        // Assert
        resolver1.Should().BeSameAs(resolver2);
    }
}
