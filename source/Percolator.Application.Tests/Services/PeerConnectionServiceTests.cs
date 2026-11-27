using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using NUnit.Framework;
using Percolator.Application.Connections;
using Percolator.Application.Handshake;
using Percolator.Application.Ingress;
using Percolator.Application.Invitations;
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
        public PublicIdentityId LastRequesterId { get; private set; }
        public PublicIdentityId LastRequestedPeerId { get; private set; }
        public RelayEndpoint? LastRequestedEndpoint { get; private set; }

        public Task<DomainResult<PreKeyBundle>> FetchConsumableBundleAsync(
            PublicIdentityId requesterId,
            PublicIdentityId peerId,
            DeviceId? deviceId = null,
            RelayEndpoint? relayEndpoint = null,
            CancellationToken ct = default)
        {
            LastRequesterId = requesterId;
            LastRequestedPeerId = peerId;
            LastRequestedEndpoint = relayEndpoint;

            if (ErrorToReturn.HasValue)
            {
                return Task.FromResult(DomainResult<PreKeyBundle>.Failure(ErrorToReturn.Value));
            }

            return Task.FromResult(DomainResult<PreKeyBundle>.Success(BundleToReturn!));
        }

        public Task<DomainResult<PreKeyBundle>> FetchAuthenticatedAsync(
            PublicIdentityId callerOwnerId,
            RelayEndpoint relayEndpoint,
            BlindedRoutingToken targetRoutingToken,
            DeviceId? deviceId = null,
            CancellationToken ct = default)
        {
            LastRequestedPeerId = callerOwnerId;
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
        public RelayEndpoint? LastRelayEndpoint { get; private set; }
        public PreKeyBundle? LastRemoteBundle { get; private set; }

        public ValueTask<DomainResult<DirectRatchetSession>> InitiateHandshakeAsync(
            PublicIdentityId ownerId,
            DeviceId ownerDeviceId,
            PublicIdentityId remotePeerId,
            RelayEndpoint relayEndpoint,
            PreKeyBundle remoteBundle,
            AppId? initialAppId = null,
            ReadOnlyMemory<byte>? initialPayload = null,
            CancellationToken ct = default)
        {
            LastOwnerId = ownerId;
            LastRemotePeerId = remotePeerId;
            LastRelayEndpoint = relayEndpoint;
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

    private sealed class FakeInitialHandshakePayloadProvider : IInitialHandshakePayloadProvider
    {
        public ChannelId? LastChannelId { get; private set; }
        public PublicIdentityId? LastLocalIdentityId { get; private set; }
        public (AppId AppId, ReadOnlyMemory<byte> Payload)? PayloadToReturn { get; set; } =
            (AppId.Chat, new byte[] { 1, 2, 3 });

        public ValueTask<(AppId AppId, ReadOnlyMemory<byte> Payload)?> CreateInitialPayloadAsync(
            PublicIdentityId localIdentityId,
            ChannelId channelId,
            CancellationToken ct = default)
        {
            LastLocalIdentityId = localIdentityId;
            LastChannelId = channelId;
            return ValueTask.FromResult(PayloadToReturn);
        }
    }

    private sealed class FakeOutboundConnectionWorkflow : IOutboundConnectionWorkflow
    {
        public DomainResult<OutboundConnectionResult> Result { get; set; } =
            DomainResult<OutboundConnectionResult>.Success(new OutboundConnectionResult(
                OutboundConnectionStatus.Connected,
                PublicIdentityId.New(),
                null!));

        public PublicIdentityId LastOwnerId { get; private set; }
        public PeerInvitationPayload? LastPayload { get; private set; }

        public Task<DomainResult<OutboundConnectionResult>> ConnectAsync(
            PublicIdentityId ownerId,
            PeerInvitationPayload payload,
            TimeSpan probeTimeout,
            CancellationToken ct = default)
        {
            LastOwnerId = ownerId;
            LastPayload = payload;
            return Task.FromResult(Result);
        }
    }

    private PublicIdentityId _localId;
    private DeviceId _localDeviceId;
    private FakePreKeyBundleProvider _bundleProvider = null!;
    private FakeHandshakeService _handshakeService = null!;
    private FakeChannelRepository _channelRepository = null!;
    private TestDateTimeProvider _timeProvider = null!;
    private InMemoryPeerMailboxRoutingRepository _routingRepository = null!;
    private InMemoryPeerContactRepository _contactRepository = null!;
    private FakeEventPublisher _eventPublisher = null!;
    private FakeInitialHandshakePayloadProvider _initialPayloadProvider = null!;
    private FakeOutboundConnectionWorkflow _outboundWorkflow = null!;
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
        _routingRepository = new InMemoryPeerMailboxRoutingRepository();
        _contactRepository = new InMemoryPeerContactRepository();
        _eventPublisher = new FakeEventPublisher();
        _initialPayloadProvider = new FakeInitialHandshakePayloadProvider();
        _outboundWorkflow = new FakeOutboundConnectionWorkflow();

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
            _routingRepository,
            _contactRepository,
            _eventPublisher,
            _initialPayloadProvider,
            _outboundWorkflow);
    }

    [Test]
    public void Constructor_NullDependencies_ThrowsArgumentNullException()
    {
        var action1 = () => new PeerConnectionService(_localId, _localDeviceId, null!, _handshakeService, _channelRepository, _timeProvider, _routingRepository, _contactRepository, _eventPublisher, _initialPayloadProvider, _outboundWorkflow);
        var action2 = () => new PeerConnectionService(_localId, _localDeviceId, _bundleProvider, null!, _channelRepository, _timeProvider, _routingRepository, _contactRepository, _eventPublisher, _initialPayloadProvider, _outboundWorkflow);
        var action3 = () => new PeerConnectionService(_localId, _localDeviceId, _bundleProvider, _handshakeService, null!, _timeProvider, _routingRepository, _contactRepository, _eventPublisher, _initialPayloadProvider, _outboundWorkflow);
        var action4 = () => new PeerConnectionService(_localId, _localDeviceId, _bundleProvider, _handshakeService, _channelRepository, null!, _routingRepository, _contactRepository, _eventPublisher, _initialPayloadProvider, _outboundWorkflow);
        var action5 = () => new PeerConnectionService(_localId, _localDeviceId, _bundleProvider, _handshakeService, _channelRepository, _timeProvider, null!, _contactRepository, _eventPublisher, _initialPayloadProvider, _outboundWorkflow);
        var action6 = () => new PeerConnectionService(_localId, _localDeviceId, _bundleProvider, _handshakeService, _channelRepository, _timeProvider, _routingRepository, null!, _eventPublisher, _initialPayloadProvider, _outboundWorkflow);
        var action7 = () => new PeerConnectionService(_localId, _localDeviceId, _bundleProvider, _handshakeService, _channelRepository, _timeProvider, _routingRepository, _contactRepository, null!, _initialPayloadProvider, _outboundWorkflow);
        var action8 = () => new PeerConnectionService(_localId, _localDeviceId, _bundleProvider, _handshakeService, _channelRepository, _timeProvider, _routingRepository, _contactRepository, _eventPublisher, null!, _outboundWorkflow);
        var action9 = () => new PeerConnectionService(_localId, _localDeviceId, _bundleProvider, _handshakeService, _channelRepository, _timeProvider, _routingRepository, _contactRepository, _eventPublisher, _initialPayloadProvider, null!);

        action1.Should().Throw<ArgumentNullException>().WithParameterName("preKeyBundleProvider");
        action2.Should().Throw<ArgumentNullException>().WithParameterName("handshakeService");
        action3.Should().Throw<ArgumentNullException>().WithParameterName("channelRepository");
        action4.Should().Throw<ArgumentNullException>().WithParameterName("timeProvider");
        action5.Should().Throw<ArgumentNullException>().WithParameterName("routingRepository");
        action6.Should().Throw<ArgumentNullException>().WithParameterName("contactRepository");
        action7.Should().Throw<ArgumentNullException>().WithParameterName("eventPublisher");
        action8.Should().Throw<ArgumentNullException>().WithParameterName("initialPayloadProvider");
        action9.Should().Throw<ArgumentNullException>().WithParameterName("outboundWorkflow");
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
    public async Task ConnectToPeerAsync_MissingRelayEndpoint_ReturnsFailure()
    {
        var targetPeerId = PublicIdentityId.New();

        var result = await _service.ConnectToPeerAsync(targetPeerId);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("MISSING_RELAY_ENDPOINT");
    }

    [Test]
    public async Task ConnectToPeerAsync_BundleFetchFails_ReturnsFailure()
    {
        var targetPeerId = PublicIdentityId.New();
        var relayEndpoint = RelayEndpoint.Create("percolator://relay.example.com").Value;
        _bundleProvider.ErrorToReturn = new DomainError("PREKEY_NOT_FOUND", "Prekey not found.");

        var result = await _service.ConnectToPeerAsync(targetPeerId, relayEndpoint: relayEndpoint);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("PREKEY_NOT_FOUND");
    }

    [Test]
    public async Task ConnectToPeerAsync_HandshakeFails_ReturnsFailure()
    {
        var targetPeerId = PublicIdentityId.New();
        var relayEndpoint = RelayEndpoint.Create("percolator://relay.example.com").Value;
        _handshakeService.ErrorToReturn = new DomainError("HANDSHAKE_INIT_FAILED", "Key agreement failed.");

        var result = await _service.ConnectToPeerAsync(targetPeerId, relayEndpoint: relayEndpoint);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("HANDSHAKE_INIT_FAILED");
    }

    [Test]
    public async Task ConnectToPeerAsync_Success_CreatesDirectChannelAndReturnsResult()
    {
        var targetPeerId = PublicIdentityId.New();
        var relayEndpoint = RelayEndpoint.Create("percolator://relay.example.com").Value;

        var result = await _service.ConnectToPeerAsync(targetPeerId, relayEndpoint: relayEndpoint);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().NotBeNull();
        result.Value!.RemoteDeviceId.Should().Be(_dummyBundle.DeviceId);

        _bundleProvider.LastRequesterId.Should().Be(_localId);
        _bundleProvider.LastRequestedPeerId.Should().Be(targetPeerId);
        _bundleProvider.LastRequestedEndpoint.Should().Be(relayEndpoint);

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
        var relayEndpoint = RelayEndpoint.Create("percolator://relay.example.com").Value;
        var existingChannelId = ChannelId.DeterministicForDirect(_localId, targetPeerId, relayEndpoint);
        var existingChannel = DirectChannel.Create(existingChannelId, _localId, targetPeerId, _timeProvider, relayEndpoint: relayEndpoint).Value!;
        await _channelRepository.SaveDirectAsync(existingChannel);

        var result = await _service.ConnectToPeerAsync(targetPeerId, relayEndpoint: relayEndpoint);

        result.IsSuccess.Should().BeTrue();
        result.Value!.ChannelId.Should().Be(existingChannelId);
    }

    [Test]
    public async Task ConnectWithInvitationAsync_NullInvitation_ThrowsArgumentNullException()
    {
        var action = () => _service.ConnectWithInvitationAsync(_localId, null!);
        await action.Should().ThrowAsync<ArgumentNullException>();
    }

    [Test]
    public async Task ConnectWithInvitationAsync_WorkflowFails_ReturnsFailure()
    {
        var targetPeerId = PublicIdentityId.New();
        var routingToken = BlindedRoutingToken.New();
        var relayEndpoint = RelayEndpoint.Create("percolator://relay.example.com").Value;
        var identityKey = IdentityKey.FromSpan(RandomNumberGenerator.GetBytes(32));
        var invitation = PeerInvitation.Create(targetPeerId, identityKey, relayEndpoint, routingToken).Value;

        _outboundWorkflow.Result = DomainResult<OutboundConnectionResult>.Failure(
            new DomainError("WORKFLOW_FAILED", "Workflow rejected connection."));

        var result = await _service.ConnectWithInvitationAsync(_localId, invitation);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("WORKFLOW_FAILED");
    }

    [Test]
    public async Task ConnectWithInvitationAsync_Success_DerivesExchangeDeterministicChannelId()
    {
        var targetPeerId = PublicIdentityId.New();
        var routingToken = BlindedRoutingToken.New();
        var relayEndpoint = RelayEndpoint.Create("percolator://relay.example.com").Value;
        var identityKey = IdentityKey.FromSpan(RandomNumberGenerator.GetBytes(32));
        var invitation = PeerInvitation.Create(targetPeerId, identityKey, relayEndpoint, routingToken).Value;

        var payload = PeerInvitationPayload.FromDomain(invitation);
        _outboundWorkflow.Result = DomainResult<OutboundConnectionResult>.Success(
            new OutboundConnectionResult(OutboundConnectionStatus.Connected, targetPeerId, payload));

        var result = await _service.ConnectWithInvitationAsync(_localId, invitation);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().NotBeNull();

        var expectedId = ChannelId.DeterministicForDirect(_localId, targetPeerId, relayEndpoint);
        result.Value!.ChannelId.Should().Be(expectedId);
        result.Value.RemoteDeviceId.Should().Be(DeviceId.Primary);

        _outboundWorkflow.LastOwnerId.Should().Be(_localId);
        _outboundWorkflow.LastPayload.Should().Be(payload);
    }
}
