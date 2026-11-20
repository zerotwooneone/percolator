using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using NUnit.Framework;
using Percolator.Application.Handshake;
using Percolator.Application.Ingress;
using Percolator.Application.Ports;
using Percolator.Application.Services;
using Percolator.Application.Tests.Routing;
using Percolator.Application.Tests.TestDoubles;
using Percolator.Domain.Channels.Model;
using Percolator.Domain.Channels.Ports;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Common;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.Model;
using Percolator.Domain.Security.ValueObjects;
using Percolator.PluginSdk;
using Percolator.PluginSdk.Profiles;

namespace Percolator.Application.Tests.Services;

[TestFixture]
public sealed class PeerConnectionServiceTests
{
    private sealed class FakePreKeyBundleProvider : IPreKeyBundleProvider
    {
        public PreKeyBundle? BundleToReturn { get; set; }
        public DomainError? ErrorToReturn { get; set; }
        public PublicIdentityId LastRequestedPeerId { get; private set; }
        public RelayEndpoint? LastRequestedEndpoint { get; private set; }

        public Task<DomainResult<PreKeyBundle>> FetchConsumableBundleAsync(
            PublicIdentityId peerId,
            DeviceId? deviceId = null,
            RelayEndpoint? relayEndpoint = null,
            CancellationToken ct = default)
        {
            LastRequestedPeerId = peerId;
            LastRequestedEndpoint = relayEndpoint;

            if (ErrorToReturn.HasValue)
            {
                return Task.FromResult(DomainResult<PreKeyBundle>.Failure(ErrorToReturn.Value));
            }

            return Task.FromResult(DomainResult<PreKeyBundle>.Success(BundleToReturn!));
        }
    }

    private sealed class FakeHandshakeService : IHandshakeService
    {
        public DomainError? ErrorToReturn { get; set; }
        public PublicIdentityId LastOwnerId { get; private set; }
        public PublicIdentityId LastRemotePeerId { get; private set; }
        public PreKeyBundle? LastRemoteBundle { get; private set; }

        public ValueTask<DomainResult<DirectRatchetSession>> InitiateHandshakeAsync(
            PublicIdentityId ownerId,
            DeviceId ownerDeviceId,
            PublicIdentityId remotePeerId,
            PreKeyBundle remoteBundle,
            AppId? initialAppId = null,
            ReadOnlyMemory<byte>? initialPayload = null,
            CancellationToken ct = default)
        {
            LastOwnerId = ownerId;
            LastRemotePeerId = remotePeerId;
            LastRemoteBundle = remoteBundle;

            if (ErrorToReturn.HasValue)
            {
                return ValueTask.FromResult(DomainResult<DirectRatchetSession>.Failure(ErrorToReturn.Value));
            }

            var crypto = new ApplicationTestCryptoEngine();
            using var sharedKey = SharedSecret.FromSpan(RandomNumberGenerator.GetBytes(32));
            var session = DirectRatchetSession.CreateFromX3dhResponder(
                ownerId,
                ownerDeviceId,
                remotePeerId,
                remoteBundle.DeviceId,
                sharedKey,
                remoteBundle.SignedPreKey,
                crypto).Value!;

            return ValueTask.FromResult(DomainResult<DirectRatchetSession>.Success(session));
        }

        public ValueTask<DomainResult> ReceiveInvitationAsync(InboundHandshakeEnvelope envelope, CancellationToken ct = default) =>
            ValueTask.FromResult(DomainResult.Success());

        public ValueTask<DomainResult> CompletePendingHandshakeAsync(PublicIdentityId recipientId, PublicIdentityId senderId, CancellationToken ct = default) =>
            ValueTask.FromResult(DomainResult.Success());
    }

    private sealed class FakeChannelRepository : IChannelRepository
    {
        private readonly Dictionary<ChannelId, DirectChannel> _directs = [];
        private readonly Dictionary<ChannelId, GroupChannel> _groups = [];

        public Task<DirectChannel?> GetDirectByIdAsync(ChannelId id, CancellationToken cancellationToken = default) =>
            Task.FromResult(_directs.GetValueOrDefault(id));

        public Task<DirectChannel?> GetDirectByPeerAsync(PublicIdentityId ownerId, PublicIdentityId remotePeerId, CancellationToken cancellationToken = default) =>
            Task.FromResult(_directs.Values.FirstOrDefault(d => d.OwnerIdentityId == ownerId && d.RemotePeerId == remotePeerId));

        public Task<GroupChannel?> GetGroupByIdAsync(ChannelId id, CancellationToken cancellationToken = default) =>
            Task.FromResult(_groups.GetValueOrDefault(id));

        public Task<IReadOnlyList<GroupChannel>> GetAllGroupsForOwnerAsync(PublicIdentityId ownerId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<GroupChannel>>(_groups.Values.ToList());

        public Task SaveDirectAsync(DirectChannel channel, CancellationToken cancellationToken = default)
        {
            _directs[channel.Id] = channel;
            return Task.CompletedTask;
        }

        public Task SaveGroupAsync(GroupChannel channel, CancellationToken cancellationToken = default)
        {
            _groups[channel.Id] = channel;
            return Task.CompletedTask;
        }
    }

    private PublicIdentityId _localId;
    private DeviceId _localDeviceId;
    private FakePreKeyBundleProvider _bundleProvider = null!;
    private FakeHandshakeService _handshakeService = null!;
    private FakeChannelRepository _channelRepository = null!;
    private TestDateTimeProvider _timeProvider = null!;
    private InMemoryStreamRegistry _streamRegistry = null!;
    private TestPeerMailboxRoutingRepository _routingRepository = null!;
    private InMemoryPeerContactRepository _contactRepository = null!;
    private PeerConnectionService _service = null!;

    private PreKeyBundle _dummyBundle = null!;

    [SetUp]
    public void SetUp()
    {
        _localId = PublicIdentityId.New();
        _localDeviceId = DeviceId.Primary;
        _bundleProvider = new FakePreKeyBundleProvider();
        _handshakeService = new FakeHandshakeService();
        _channelRepository = new FakeChannelRepository();
        _timeProvider = new TestDateTimeProvider();
        _streamRegistry = new InMemoryStreamRegistry();
        _routingRepository = new TestPeerMailboxRoutingRepository();
        _contactRepository = new InMemoryPeerContactRepository();

        _dummyBundle = new PreKeyBundle(
            DeviceId.Primary,
            IdentityKey.FromSpan(RandomNumberGenerator.GetBytes(32)),
            DhPublicKey.FromSpan(RandomNumberGenerator.GetBytes(32)),
            DeviceLinkProof.FromSpan(RandomNumberGenerator.GetBytes(64)),
            SignedPreKeyId.First);

        _bundleProvider.BundleToReturn = _dummyBundle;

        _service = new PeerConnectionService(
            _localId,
            _localDeviceId,
            _bundleProvider,
            _handshakeService,
            _channelRepository,
            _timeProvider,
            _streamRegistry,
            _routingRepository,
            _contactRepository);
    }

    [Test]
    public void Constructor_NullDependencies_ThrowsArgumentNullException()
    {
        var action1 = () => new PeerConnectionService(_localId, _localDeviceId, null!, _handshakeService, _channelRepository, _timeProvider, _streamRegistry, _routingRepository, _contactRepository);
        var action2 = () => new PeerConnectionService(_localId, _localDeviceId, _bundleProvider, null!, _channelRepository, _timeProvider, _streamRegistry, _routingRepository, _contactRepository);
        var action3 = () => new PeerConnectionService(_localId, _localDeviceId, _bundleProvider, _handshakeService, null!, _timeProvider, _streamRegistry, _routingRepository, _contactRepository);
        var action4 = () => new PeerConnectionService(_localId, _localDeviceId, _bundleProvider, _handshakeService, _channelRepository, null!, _streamRegistry, _routingRepository, _contactRepository);
        var action5 = () => new PeerConnectionService(_localId, _localDeviceId, _bundleProvider, _handshakeService, _channelRepository, _timeProvider, null!, _routingRepository, _contactRepository);
        var action6 = () => new PeerConnectionService(_localId, _localDeviceId, _bundleProvider, _handshakeService, _channelRepository, _timeProvider, _streamRegistry, null!, _contactRepository);
        var action7 = () => new PeerConnectionService(_localId, _localDeviceId, _bundleProvider, _handshakeService, _channelRepository, _timeProvider, _streamRegistry, _routingRepository, null!);

        action1.Should().Throw<ArgumentNullException>().WithParameterName("preKeyBundleProvider");
        action2.Should().Throw<ArgumentNullException>().WithParameterName("handshakeService");
        action3.Should().Throw<ArgumentNullException>().WithParameterName("channelRepository");
        action4.Should().Throw<ArgumentNullException>().WithParameterName("timeProvider");
        action5.Should().Throw<ArgumentNullException>().WithParameterName("streamRegistry");
        action6.Should().Throw<ArgumentNullException>().WithParameterName("routingRepository");
        action7.Should().Throw<ArgumentNullException>().WithParameterName("contactRepository");
    }

    [Test]
    public async Task ConnectToPeerAsync_InvalidPeerId_ReturnsFailure()
    {
        var invalidId = default(PublicIdentityId);

        var result = await _service.ConnectToPeerAsync(invalidId);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("INVALID_PEER_ID");
    }

    [Test]
    public async Task ConnectToPeerAsync_BundleFetchFails_ReturnsFailure()
    {
        var targetPeerId = PublicIdentityId.New();
        _bundleProvider.ErrorToReturn = new DomainError("PREKEY_NOT_FOUND", "Prekey not found.");

        var result = await _service.ConnectToPeerAsync(targetPeerId);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("PREKEY_NOT_FOUND");
    }

    [Test]
    public async Task ConnectToPeerAsync_HandshakeFails_ReturnsFailure()
    {
        var targetPeerId = PublicIdentityId.New();
        _handshakeService.ErrorToReturn = new DomainError("HANDSHAKE_INIT_FAILED", "Key agreement failed.");

        var result = await _service.ConnectToPeerAsync(targetPeerId);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("HANDSHAKE_INIT_FAILED");
    }

    [Test]
    public async Task ConnectToPeerAsync_Success_CreatesDirectChannelAndReturnsResult()
    {
        var targetPeerId = PublicIdentityId.New();

        var result = await _service.ConnectToPeerAsync(targetPeerId);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().NotBeNull();
        result.Value!.RemoteDeviceId.Should().Be(_dummyBundle.DeviceId);

        var savedChannel = await _channelRepository.GetDirectByIdAsync(result.Value.ChannelId);
        savedChannel.Should().NotBeNull();
        savedChannel!.OwnerIdentityId.Should().Be(_localId);
        savedChannel.RemotePeerId.Should().Be(targetPeerId);

        var savedContact = await _contactRepository.GetByPeerIdAsync(_localId, targetPeerId);
        savedContact.Should().NotBeNull();
        savedContact!.RemotePeerId.Should().Be(targetPeerId);
        savedContact.PrimaryPublicKey.Should().Be(_dummyBundle.IdentityKey);
    }

    [Test]
    public async Task ConnectToPeerAsync_ExistingChannel_ReusesExistingChannelId()
    {
        var targetPeerId = PublicIdentityId.New();
        var existingChannelId = ChannelId.New();
        var existingChannel = DirectChannel.Create(existingChannelId, _localId, targetPeerId, _timeProvider).Value!;
        await _channelRepository.SaveDirectAsync(existingChannel);

        var result = await _service.ConnectToPeerAsync(targetPeerId);

        result.IsSuccess.Should().BeTrue();
        result.Value!.ChannelId.Should().Be(existingChannelId);
    }

    [Test]
    public async Task ConnectToDirectPeerAsync_InvalidPeerId_ReturnsFailure()
    {
        var invalidId = default(PublicIdentityId);
        var routingToken = BlindedRoutingToken.New();
        var deliveryToken = DeliveryToken.GenerateRandom();
        var endpoint = new Uri("https://192.168.1.50:5100");

        var result = await _service.ConnectToDirectPeerAsync(invalidId, routingToken, deliveryToken, endpoint);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("INVALID_PEER_ID");
    }

    [Test]
    public async Task ConnectToDirectPeerAsync_NullEndpoint_ThrowsArgumentNullException()
    {
        var targetPeerId = PublicIdentityId.New();
        var routingToken = BlindedRoutingToken.New();
        var deliveryToken = DeliveryToken.GenerateRandom();

        var action = () => _service.ConnectToDirectPeerAsync(targetPeerId, routingToken, deliveryToken, null!);

        await action.Should().ThrowAsync<ArgumentNullException>();
    }

    [Test]
    public async Task ConnectToDirectPeerAsync_RegistersStreamAndSavesRoutingCapability()
    {
        var targetPeerId = PublicIdentityId.New();
        var routingToken = BlindedRoutingToken.New();
        var deliveryToken = DeliveryToken.GenerateRandom();
        var endpoint = new Uri("https://192.168.1.100:5150");

        var result = await _service.ConnectToDirectPeerAsync(targetPeerId, routingToken, deliveryToken, endpoint);

        result.IsSuccess.Should().BeTrue();

        // Verify stream registry
        _streamRegistry.GetDirectEndpoint(targetPeerId).Should().Be(endpoint);

        // Verify routing capability saved
        var route = await _routingRepository.GetByPeerIdAsync(targetPeerId);
        route.Should().NotBeNull();
        route!.PeerId.Should().Be(targetPeerId);
        route.RoutingToken.Should().Be(routingToken);
        route.DeliveryToken.Should().Be(deliveryToken);
        route.RelayEndpoint.Should().NotBeNull();
        route.RelayEndpoint!.Value.Should().Be("percolator://192.168.1.100:5150");

        // Verify prekey bundle requested with direct endpoint
        _bundleProvider.LastRequestedPeerId.Should().Be(targetPeerId);
        _bundleProvider.LastRequestedEndpoint.Should().NotBeNull();
        _bundleProvider.LastRequestedEndpoint!.Value.Should().Be("percolator://192.168.1.100:5150");
    }

    [Test]
    public async Task ConnectToDirectPeerAsync_BundleFetchFails_ReturnsFailure()
    {
        var targetPeerId = PublicIdentityId.New();
        var routingToken = BlindedRoutingToken.New();
        var deliveryToken = DeliveryToken.GenerateRandom();
        var endpoint = new Uri("https://192.168.1.100:5150");
        _bundleProvider.ErrorToReturn = new DomainError("BUNDLE_UNREACHABLE", "Endpoint connection timeout.");

        var result = await _service.ConnectToDirectPeerAsync(targetPeerId, routingToken, deliveryToken, endpoint);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("BUNDLE_UNREACHABLE");
    }

    [Test]
    public async Task ConnectToDirectPeerAsync_HandshakeFails_ReturnsFailure()
    {
        var targetPeerId = PublicIdentityId.New();
        var routingToken = BlindedRoutingToken.New();
        var deliveryToken = DeliveryToken.GenerateRandom();
        var endpoint = new Uri("https://192.168.1.100:5150");
        _handshakeService.ErrorToReturn = new DomainError("HANDSHAKE_REJECTED", "Crypto initiation rejected.");

        var result = await _service.ConnectToDirectPeerAsync(targetPeerId, routingToken, deliveryToken, endpoint);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("HANDSHAKE_REJECTED");
    }

    [Test]
    public async Task ConnectToDirectPeerAsync_Success_DerivesDeterministicChannelId()
    {
        var targetPeerId = PublicIdentityId.New();
        var routingToken = BlindedRoutingToken.New();
        var deliveryToken = DeliveryToken.GenerateRandom();
        var endpoint = new Uri("https://10.0.0.5:5200");

        var result = await _service.ConnectToDirectPeerAsync(targetPeerId, routingToken, deliveryToken, endpoint);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().NotBeNull();

        // Channel exists in repository with deterministic ID
        var channel = await _channelRepository.GetDirectByIdAsync(result.Value!.ChannelId);
        channel.Should().NotBeNull();
        channel!.OwnerIdentityId.Should().Be(_localId);
        channel.RemotePeerId.Should().Be(targetPeerId);

        // Derive expected deterministic channel ID
        var (first, second) = _localId.Value.CompareTo(targetPeerId.Value) < 0 ? (_localId, targetPeerId) : (targetPeerId, _localId);
        Span<byte> combined = stackalloc byte[32];
        first.TryWriteBytes(combined[..16]);
        second.TryWriteBytes(combined[16..]);
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(combined, hash);
        var expectedId = ChannelId.FromBytes(hash[..16]);

        result.Value.ChannelId.Should().Be(expectedId);

        var savedContact = await _contactRepository.GetByPeerIdAsync(_localId, targetPeerId);
        savedContact.Should().NotBeNull();
        savedContact!.RemotePeerId.Should().Be(targetPeerId);
        savedContact.PrimaryPublicKey.Should().Be(_dummyBundle.IdentityKey);
    }
}
