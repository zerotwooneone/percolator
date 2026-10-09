using Percolator.Application2.Delivery;
using Percolator.Application2.Egress;
using Percolator.Application2.Ports;
using Percolator.Application2.Tests.TestDoubles;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Common;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.Model;
using Percolator.Domain.Security.ValueObjects;
using Percolator.PluginSdk;
using PluginRoute = Percolator.PluginSdk.DeliveryRoute;

namespace Percolator.Application2.Tests.Delivery;

[TestFixture]
public sealed class EgressPipelineTests
{
    private InMemoryRatchetSessionRepository _sessionRepo = null!;
    private InMemoryGroupSenderKeyRepository _groupSenderKeyRepo = null!;
    private FakeSessionWirePacker _sessionWirePacker = null!;
    private ApplicationTestCryptoEngine _cryptoEngine = null!;
    private InMemoryOutboxRepository _outboxRepo = null!;
    private InMemoryStreamRegistry _streamRegistry = null!;
    private TestDateTimeProvider _timeProvider = null!;
    private NoOpChannelLockService _channelLockService = null!;
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
        _streamRegistry = new InMemoryStreamRegistry();
        _timeProvider = new TestDateTimeProvider();
        _channelLockService = new NoOpChannelLockService();

        _egressPipeline = new OutboundEgressPipeline(
            _sessionRepo,
            _groupSenderKeyRepo,
            _sessionWirePacker,
            _cryptoEngine,
            _outboxRepo,
            _streamRegistry,
            _timeProvider,
            _channelLockService);

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
            _bobId,
            _bobDeviceId,
            IdentityKey.FromBytes(new byte[32]),
            bobSignedPreKey.PublicKey,
            DeviceLinkProof.FromBytes(new byte[64]));

        var aliceSession = DirectRatchetSession.InitiateOutbound(_aliceId, _aliceDeviceId, bundle, _cryptoEngine).Value!;
        await _sessionRepo.SaveSessionAsync(aliceSession);
        return aliceSession;
    }

    [Test]
    public async Task DispatchAsync_AlwaysPersistsJobToOutboxFirst()
    {
        // Arrange
        await SetupActiveRatchetSessionAsync();
        var context = new OutboundPayloadContext(
            _channelId,
            _aliceId,
            _bobId,
            _bobDeviceId,
            AppId.Chat,
            "Hello Bob"u8.ToArray(),
            PluginRoute.DirectP2P);

        // Act
        var result = await _egressPipeline.SendPayloadAsync(context);

        // Assert
        result.IsSuccess.Should().BeTrue();
        _outboxRepo.AllJobs.Should().HaveCount(1);
        var job = _outboxRepo.AllJobs.First();
        job.ChannelId.Should().Be(_channelId);
        job.OwnerIdentityId.Should().Be(_aliceId);
        job.RecipientIdentityId.Should().Be(_bobId);
    }

    [Test]
    public async Task DispatchAsync_WhenStreamOnline_TriggersImmediateStreamDispatch()
    {
        // Arrange
        await SetupActiveRatchetSessionAsync();
        _streamRegistry.SetStreamActive(_bobId, true);

        var context = new OutboundPayloadContext(
            _channelId,
            _aliceId,
            _bobId,
            _bobDeviceId,
            AppId.Chat,
            "Stream Fast-Path"u8.ToArray(),
            PluginRoute.DirectP2P);

        // Act
        var result = await _egressPipeline.SendPayloadAsync(context);

        // Assert
        result.IsSuccess.Should().BeTrue();
        _streamRegistry.WrittenPayloads.Should().HaveCount(1);
        _streamRegistry.WrittenPayloads[0].TargetId.Should().Be(_bobId);

        var job = _outboxRepo.AllJobs.First();
        job.Status.Should().Be(OutboxStatus.Delivered);
    }

    [Test]
    public async Task DispatchAsync_WhenStreamDisconnected_LeavesJobPendingWithBackoff()
    {
        // Arrange
        await SetupActiveRatchetSessionAsync();
        _streamRegistry.SetStreamActive(_bobId, false); // offline stream

        var context = new OutboundPayloadContext(
            _channelId,
            _aliceId,
            _bobId,
            _bobDeviceId,
            AppId.Chat,
            "Stream Offline"u8.ToArray(),
            PluginRoute.DirectP2P);

        // Act
        var result = await _egressPipeline.SendPayloadAsync(context);

        // Assert
        result.IsSuccess.Should().BeTrue();
        _streamRegistry.WrittenPayloads.Should().BeEmpty();

        var job = _outboxRepo.AllJobs.First();
        job.Status.Should().Be(OutboxStatus.Pending);
    }

    [Test]
    public async Task DispatchAsync_WhenStreamBackpressured_SpillsToOutboxWithBackoff()
    {
        // Arrange
        await SetupActiveRatchetSessionAsync();
        _streamRegistry.SetStreamActive(_bobId, true);
        _streamRegistry.SetBackpressured(_bobId, true);

        var context = new OutboundPayloadContext(
            _channelId,
            _aliceId,
            _bobId,
            _bobDeviceId,
            AppId.Chat,
            "Stream Backpressured"u8.ToArray(),
            PluginRoute.DirectP2P);

        // Act
        var result = await _egressPipeline.SendPayloadAsync(context);

        // Assert
        result.IsSuccess.Should().BeTrue();
        _streamRegistry.WrittenPayloads.Should().BeEmpty();

        var job = _outboxRepo.AllJobs.First();
        job.Status.Should().Be(OutboxStatus.Pending);
        job.RetryCount.Should().Be(1);
    }

    [Test]
    public async Task DispatchGroupBroadcast_DispatchesSingleFramedPayload()
    {
        // Arrange
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
