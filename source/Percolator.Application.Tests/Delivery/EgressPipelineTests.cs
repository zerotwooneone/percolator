using Percolator.Application.Delivery;
using Percolator.Application.Egress;
using Percolator.Application.Tests.TestDoubles;
using Percolator.Domain.Channels.Model;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.Model;
using Percolator.Domain.Security.ValueObjects;
using Percolator.PluginSdk;
using Percolator.PluginSdk.Profiles;
using PluginRoute = Percolator.PluginSdk.DeliveryRoute;
using PublicIdentityId = Percolator.Domain.Identities.PublicIdentityId;

namespace Percolator.Application.Tests.Delivery;

[TestFixture]
public sealed class EgressPipelineTests
{
    private InMemoryRatchetSessionRepository _sessionRepo = null!;
    private InMemoryGroupSenderKeyRepository _groupSenderKeyRepo = null!;
    private FakeSessionWirePacker _sessionWirePacker = null!;
    private ApplicationTestCryptoEngine _cryptoEngine = null!;
    private InMemoryOutboxRepository _outboxRepo = null!;
    private TestDateTimeProvider _timeProvider = null!;
    private NoOpChannelLockService _channelLockService = null!;
    private InMemoryPeerMailboxRoutingRepository _routingRepo = null!;
    private InMemoryChannelRepository _channelRepo = null!;
    private FakeEventPublisher _eventPublisher = null!;
    private OutboundEgressPipeline _egressPipeline = null!;

    private PublicIdentityId _aliceId;
    private DeviceId _aliceDeviceId;
    private PublicIdentityId _bobId;
    private DeviceId _bobDeviceId;
    private ChannelId _channelId;

    [SetUp]
    public void SetUp()
    {
        _sessionRepo = new InMemoryRatchetSessionRepository();
        _groupSenderKeyRepo = new InMemoryGroupSenderKeyRepository();
        _sessionWirePacker = new FakeSessionWirePacker();
        _cryptoEngine = new ApplicationTestCryptoEngine();
        _outboxRepo = new InMemoryOutboxRepository();
        _routingRepo = new InMemoryPeerMailboxRoutingRepository();
        _channelRepo = new InMemoryChannelRepository();
        _timeProvider = new TestDateTimeProvider();
        _channelLockService = new NoOpChannelLockService();
        _eventPublisher = new FakeEventPublisher();

        _egressPipeline = new OutboundEgressPipeline(
            _sessionRepo,
            _groupSenderKeyRepo,
            _sessionWirePacker,
            _cryptoEngine,
            _outboxRepo,
            _routingRepo,
            _channelRepo,
            _timeProvider,
            _channelLockService,
            _eventPublisher);

        _aliceId = PublicIdentityId.New();
        _aliceDeviceId = DeviceId.Primary;
        _bobId = PublicIdentityId.New();
        _bobDeviceId = new DeviceId(2);
        _channelId = ChannelId.New();
    }

    private async Task<DirectRatchetSession> SetupActiveRatchetSessionAsync()
    {
        var bobSignedPreKey = _cryptoEngine.GenerateEphemeralKeyPair();
        var bundle = new PreKeyBundle(
            _bobDeviceId,
            IdentityKey.FromBytes(new byte[32]),
            bobSignedPreKey.PublicKey,
            DeviceLinkProof.FromBytes(new byte[64]),
            SignedPreKeyId.First);

        var aliceSession = DirectRatchetSession.InitiateOutbound(_aliceId, _aliceDeviceId, _bobId, bundle, _cryptoEngine).Value!;
        await _sessionRepo.SaveSessionAsync(aliceSession);
        return aliceSession;
    }

    [Test]
    public async Task DispatchAsync_AlwaysPersistsJobToOutboxFirst()
    {
        // Arrange
        await SetupActiveRatchetSessionAsync();
        var relayEndpoint = RelayEndpoint.Create("percolator://relay.example.com").Value;
        var directChannel = DirectChannel.Create(_channelId, _aliceId, _bobId, _timeProvider, relayEndpoint: relayEndpoint).Value!;
        await _channelRepo.SaveDirectAsync(directChannel);
        await _routingRepo.SaveAsync(new PeerMailboxRoutingCapability(_bobId, BlindedRoutingToken.New(), DeliveryToken.GenerateRandom(), relayEndpoint, _timeProvider.UtcNow));

        var context = new OutboundPayloadContext(
            _channelId,
            _aliceId,
            _bobId,
            _bobDeviceId,
            AppId.Chat,
            "Hello Bob"u8.ToArray(),
            PluginRoute.RelayedOneToOne);

        // Act
        var result = await _egressPipeline.SendPayloadAsync(context);

        // Assert
        result.IsSuccess.Should().BeTrue();
        _outboxRepo.AllJobs.Should().HaveCount(1);
        var job = _outboxRepo.AllJobs.First();
        job.ChannelId.Should().Be(_channelId);
        job.OwnerIdentityId.Should().Be(_aliceId);
        job.RecipientIdentityId.Should().Be(_bobId);
        job.Status.Should().Be(OutboxStatus.Pending);
    }

    [Test]
    public async Task DispatchGroupBroadcast_DispatchesSingleFramedPayload()
    {
        // Arrange
        var relayEndpoint = RelayEndpoint.Create("percolator://relay.example.com").Value;
        var groupChannel = GroupChannel.CreateGenesis(_channelId, _aliceId, "Test Group", _timeProvider, relayEndpoint: relayEndpoint).Value!;
        await _channelRepo.SaveGroupAsync(groupChannel);

        using var initialChain = ChainKey.FromSpan(new byte[32]);
        var (signingPriv, signingPub) = _cryptoEngine.GenerateEphemeralKeyPair();
        var ratchet = new GroupSenderKeyRatchet(
            _channelId,
            _aliceId,
            DeviceId.Primary,
            initialChain,
            signingPrivateKey: signingPriv,
            authorSigningPublicKey: IdentityKey.FromSpan(signingPub.Span));

        await _groupSenderKeyRepo.SaveSenderKeyRatchetAsync(ratchet);

        var context = new OutboundPayloadContext(
            _channelId,
            _aliceId,
            null, // Group broadcast (no single recipient)
            default,
            AppId.Chat,
            "Group Announcement"u8.ToArray(),
            PluginRoute.RelayedGroup);

        // Act
        var result = await _egressPipeline.SendPayloadAsync(context);

        // Assert
        result.IsSuccess.Should().BeTrue();
        _outboxRepo.AllJobs.Should().HaveCount(1);
        var job = _outboxRepo.AllJobs.First();
        job.RecipientIdentityId.Should().BeNull();
        job.Route.Type.Should().Be(DeliveryRouteType.RelayedGroup);
    }
}
