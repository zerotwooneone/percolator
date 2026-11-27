namespace Percolator.Application.Tests.Connections;

using System;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Percolator.Application.Connections;
using Percolator.Application.Handshake;
using Percolator.Application.Ingress;
using Percolator.Application.Invitations;
using Percolator.Application.Ports;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Common;
using Percolator.Domain.Delivery.Model;
using Percolator.Domain.Delivery.Ports;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities;
using Percolator.Domain.Identities.Model;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.Model;
using Percolator.Domain.Security.ValueObjects;
using Percolator.Application.Tests.TestDoubles;
using Percolator.PluginSdk;
using Percolator.PluginSdk.Profiles;

[TestFixture]
public sealed class OutboundConnectionWorkflowTests
{
    private FakeRelayDirectory _relayDirectory = null!;
    private InMemoryPeerContactRepository _contactRepo = null!;
    private FakePreKeyBundleProvider _bundleProvider = null!;
    private ApplicationTestCryptoEngine _cryptoEngine = null!;
    private InMemoryPeerMailboxRoutingRepository _routingRepo = null!;
    private InMemoryChannelRepository _channelRepo = null!;
    private FakeInitialHandshakePayloadProvider _initialPayloadProvider = null!;
    private FakeHandshakeService _handshakeService = null!;
    private TestDateTimeProvider _timeProvider = null!;
    private OutboundConnectionWorkflow _sut = null!;

    private PublicIdentityId _ownerId;
    private PublicIdentityId _peerId;
    private RelayEndpoint _relayEndpoint = null!;
    private BlindedRoutingToken _routingToken;
    private IdentityKey _peerIdentityKey = null!;
    private PreKeyBundle _dummyBundle = null!;
    private PeerInvitationPayload _validPayload = null!;

    [SetUp]
    public void SetUp()
    {
        _relayDirectory = new FakeRelayDirectory();
        _contactRepo = new InMemoryPeerContactRepository();
        _bundleProvider = new FakePreKeyBundleProvider();
        _cryptoEngine = new ApplicationTestCryptoEngine();
        _routingRepo = new InMemoryPeerMailboxRoutingRepository();
        _channelRepo = new InMemoryChannelRepository();
        _initialPayloadProvider = new FakeInitialHandshakePayloadProvider();
        _handshakeService = new FakeHandshakeService();
        _timeProvider = new TestDateTimeProvider();

        _ownerId = PublicIdentityId.New();
        _peerId = PublicIdentityId.New();
        _relayEndpoint = RelayEndpoint.Create("percolator://relay.example.com").Value;
        _routingToken = BlindedRoutingToken.New();
        _peerIdentityKey = IdentityKey.FromSpan(RandomNumberGenerator.GetBytes(32));

        var relayId = PublicIdentityId.New();
        _relayDirectory.AddEntry(new RelayDirectoryEntry(
            relayId,
            "Example Relay",
            _relayEndpoint,
            false,
            new[] { _ownerId }));

        _dummyBundle = new PreKeyBundle(
            DeviceId.Primary,
            _peerIdentityKey,
            DhPublicKey.FromSpan(RandomNumberGenerator.GetBytes(32)),
            DeviceLinkProof.FromSpan(RandomNumberGenerator.GetBytes(64)),
            SignedPreKeyId.First);

        _bundleProvider.BundleToReturn = _dummyBundle;

        _validPayload = new PeerInvitationPayload(
            _peerId,
            _peerIdentityKey,
            _relayEndpoint,
            _routingToken);

        _sut = new OutboundConnectionWorkflow(
            _relayDirectory,
            _contactRepo,
            _bundleProvider,
            _cryptoEngine,
            _routingRepo,
            _channelRepo,
            _initialPayloadProvider,
            _handshakeService,
            _timeProvider,
            NullLogger<OutboundConnectionWorkflow>.Instance);
    }

    [Test]
    public void Constructor_NullDependencies_ThrowsArgumentNullException()
    {
        var logger = NullLogger<OutboundConnectionWorkflow>.Instance;
        Action act1 = () => new OutboundConnectionWorkflow(null!, _contactRepo, _bundleProvider, _cryptoEngine, _routingRepo, _channelRepo, _initialPayloadProvider, _handshakeService, _timeProvider, logger);
        Action act2 = () => new OutboundConnectionWorkflow(_relayDirectory, null!, _bundleProvider, _cryptoEngine, _routingRepo, _channelRepo, _initialPayloadProvider, _handshakeService, _timeProvider, logger);
        Action act3 = () => new OutboundConnectionWorkflow(_relayDirectory, _contactRepo, null!, _cryptoEngine, _routingRepo, _channelRepo, _initialPayloadProvider, _handshakeService, _timeProvider, logger);
        Action act4 = () => new OutboundConnectionWorkflow(_relayDirectory, _contactRepo, _bundleProvider, null!, _routingRepo, _channelRepo, _initialPayloadProvider, _handshakeService, _timeProvider, logger);
        Action act5 = () => new OutboundConnectionWorkflow(_relayDirectory, _contactRepo, _bundleProvider, _cryptoEngine, null!, _channelRepo, _initialPayloadProvider, _handshakeService, _timeProvider, logger);
        Action act6 = () => new OutboundConnectionWorkflow(_relayDirectory, _contactRepo, _bundleProvider, _cryptoEngine, _routingRepo, null!, _initialPayloadProvider, _handshakeService, _timeProvider, logger);
        Action act7 = () => new OutboundConnectionWorkflow(_relayDirectory, _contactRepo, _bundleProvider, _cryptoEngine, _routingRepo, _channelRepo, null!, _handshakeService, _timeProvider, logger);
        Action act8 = () => new OutboundConnectionWorkflow(_relayDirectory, _contactRepo, _bundleProvider, _cryptoEngine, _routingRepo, _channelRepo, _initialPayloadProvider, null!, _timeProvider, logger);
        Action act9 = () => new OutboundConnectionWorkflow(_relayDirectory, _contactRepo, _bundleProvider, _cryptoEngine, _routingRepo, _channelRepo, _initialPayloadProvider, _handshakeService, null!, logger);
        Action act10 = () => new OutboundConnectionWorkflow(_relayDirectory, _contactRepo, _bundleProvider, _cryptoEngine, _routingRepo, _channelRepo, _initialPayloadProvider, _handshakeService, _timeProvider, null!);

        act1.Should().Throw<ArgumentNullException>().WithParameterName("relayDirectory");
        act2.Should().Throw<ArgumentNullException>().WithParameterName("contactRepo");
        act3.Should().Throw<ArgumentNullException>().WithParameterName("preKeyProvider");
        act4.Should().Throw<ArgumentNullException>().WithParameterName("cryptoEngine");
        act5.Should().Throw<ArgumentNullException>().WithParameterName("routingRepo");
        act6.Should().Throw<ArgumentNullException>().WithParameterName("channelRepo");
        act7.Should().Throw<ArgumentNullException>().WithParameterName("initialPayloadProvider");
        act8.Should().Throw<ArgumentNullException>().WithParameterName("handshakeService");
        act9.Should().Throw<ArgumentNullException>().WithParameterName("timeProvider");
        act10.Should().Throw<ArgumentNullException>().WithParameterName("logger");
    }

    [Test]
    public async Task ConnectAsync_NullPayload_ThrowsArgumentNullException()
    {
        var act = () => _sut.ConnectAsync(_ownerId, null!, TimeSpan.FromSeconds(3));
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Test]
    public async Task ConnectAsync_ExchangeNotLinked_ReturnsFailure()
    {
        _relayDirectory.ClearEntries();

        var result = await _sut.ConnectAsync(_ownerId, _validPayload, TimeSpan.FromSeconds(3));

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("EXCHANGE_NOT_LINKED");
    }

    [Test]
    public async Task ConnectAsync_ContactBlocked_ReturnsFailure()
    {
        var contact = new PeerContact(
            _ownerId,
            _peerId,
            ContactNickname.Create("Blocked", _peerId),
            PeerTrustLevel.Blocked,
            _timeProvider.UtcNow,
            _peerIdentityKey);
        await _contactRepo.SaveAsync(contact);

        var result = await _sut.ConnectAsync(_ownerId, _validPayload, TimeSpan.FromSeconds(3));

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("CONTACT_BLOCKED");
    }

    [Test]
    public async Task ConnectAsync_IdentityKeyChanged_ReturnsFailure()
    {
        var differentKey = IdentityKey.FromSpan(RandomNumberGenerator.GetBytes(32));
        var contact = new PeerContact(
            _ownerId,
            _peerId,
            ContactNickname.Create("Other", _peerId),
            PeerTrustLevel.Tofu,
            _timeProvider.UtcNow,
            differentKey);
        await _contactRepo.SaveAsync(contact);

        var result = await _sut.ConnectAsync(_ownerId, _validPayload, TimeSpan.FromSeconds(3));

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("IDENTITY_KEY_CHANGED");
    }

    [Test]
    public async Task ConnectAsync_PreKeyIdentityMismatch_ReturnsSecurityError()
    {
        var mismatchKey = IdentityKey.FromSpan(RandomNumberGenerator.GetBytes(32));
        _bundleProvider.BundleToReturn = new PreKeyBundle(
            DeviceId.Primary,
            mismatchKey,
            DhPublicKey.FromSpan(RandomNumberGenerator.GetBytes(32)),
            DeviceLinkProof.FromSpan(RandomNumberGenerator.GetBytes(64)),
            SignedPreKeyId.First);

        var result = await _sut.ConnectAsync(_ownerId, _validPayload, TimeSpan.FromSeconds(3));

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("IDENTITY_KEY_MISMATCH");
    }

    [Test]
    public async Task ConnectAsync_PreKeyBundleFetchFails_ReturnsFailure()
    {
        _bundleProvider.ErrorToReturn = new DomainError("PREKEY_UNAVAILABLE", "Remote bundle not found");

        var result = await _sut.ConnectAsync(_ownerId, _validPayload, TimeSpan.FromSeconds(3));

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("PREKEY_UNAVAILABLE");
    }

    [Test]
    public async Task ConnectAsync_HandshakeFails_ReturnsFailure()
    {
        _handshakeService.ErrorToReturn = new DomainError("HANDSHAKE_FAILED", "Failed to initiate handshake");

        var result = await _sut.ConnectAsync(_ownerId, _validPayload, TimeSpan.FromSeconds(3));

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("HANDSHAKE_FAILED");
    }

    [Test]
    public async Task ConnectAsync_SuccessfulConnection_ReturnsConnectedResult()
    {
        var result = await _sut.ConnectAsync(_ownerId, _validPayload, TimeSpan.FromSeconds(3));

        result.IsSuccess.Should().BeTrue();
        result.Value!.Status.Should().Be(OutboundConnectionStatus.Connected);
        result.Value.PeerId.Should().Be(_peerId);
        result.Value.Invitation.Should().Be(_validPayload);

        var expectedChannelId = ChannelId.DeterministicForDirect(_ownerId, _peerId, _relayEndpoint);
        var channel = await _channelRepo.GetDirectByIdAsync(expectedChannelId);
        channel.Should().NotBeNull();
        channel!.OwnerIdentityId.Should().Be(_ownerId);
        channel.RemotePeerId.Should().Be(_peerId);

        var contact = await _contactRepo.GetByPeerIdAsync(_ownerId, _peerId);
        contact.Should().NotBeNull();

        var route = await _routingRepo.GetByPeerIdAsync(_peerId);
        route.Should().NotBeNull();
        route!.RelayEndpoint.Should().Be(_relayEndpoint);
    }

    private sealed class FakePreKeyBundleProvider : IPreKeyBundleProvider
    {
        public PreKeyBundle? BundleToReturn { get; set; }
        public DomainError? ErrorToReturn { get; set; }

        public Task<DomainResult<PreKeyBundle>> FetchConsumableBundleAsync(
            PublicIdentityId requesterId,
            PublicIdentityId peerId,
            DeviceId? deviceId = null,
            RelayEndpoint? relayEndpoint = null,
            CancellationToken ct = default)
        {
            if (ErrorToReturn.HasValue) return Task.FromResult(DomainResult<PreKeyBundle>.Failure(ErrorToReturn.Value));
            return Task.FromResult(DomainResult<PreKeyBundle>.Success(BundleToReturn!));
        }

        public Task<DomainResult<PreKeyBundle>> FetchAuthenticatedAsync(
            PublicIdentityId callerOwnerId,
            RelayEndpoint relayEndpoint,
            BlindedRoutingToken targetRoutingToken,
            DeviceId? deviceId = null,
            CancellationToken ct = default)
        {
            if (ErrorToReturn.HasValue) return Task.FromResult(DomainResult<PreKeyBundle>.Failure(ErrorToReturn.Value));
            return Task.FromResult(DomainResult<PreKeyBundle>.Success(BundleToReturn!));
        }
    }

    private sealed class FakeInitialHandshakePayloadProvider : IInitialHandshakePayloadProvider
    {
        public ValueTask<(AppId AppId, ReadOnlyMemory<byte> Payload)?> CreateInitialPayloadAsync(
            PublicIdentityId localIdentityId,
            ChannelId channelId,
            CancellationToken ct = default)
        {
            return ValueTask.FromResult<(AppId AppId, ReadOnlyMemory<byte> Payload)?>((AppId.Chat, new byte[] { 1, 2, 3 }));
        }
    }

    private sealed class FakeHandshakeService : IHandshakeService
    {
        public DomainError? ErrorToReturn { get; set; }

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
            if (ErrorToReturn.HasValue)
            {
                return ValueTask.FromResult(DomainResult<DirectRatchetSession>.Failure(ErrorToReturn.Value));
            }

            return ValueTask.FromResult(DomainResult<DirectRatchetSession>.Success(null!));
        }

        public ValueTask<DomainResult> ReceiveInvitationAsync(
            InboundHandshakeEnvelope envelope,
            CancellationToken ct = default)
        {
            return ValueTask.FromResult(DomainResult.Success());
        }

        public ValueTask<DomainResult> CompletePendingHandshakeAsync(
            PublicIdentityId personaId,
            PublicIdentityId requesterId,
            CancellationToken ct = default)
        {
            return ValueTask.FromResult(DomainResult.Success());
        }
    }
}
