namespace Percolator.Application.Tests.Connections;

using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Percolator.Application.Connections;
using Percolator.Application.Ingress;
using Percolator.Application.Ports;
using Percolator.Application.Profiles;
using Percolator.Application.Tests.TestDoubles;
using Percolator.Domain.Common;
using Percolator.Domain.Identities;
using Percolator.Domain.Identities.Model;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Security.Model;
using Percolator.PluginSdk;
using Percolator.PluginSdk.Profiles;

[TestFixture]
public sealed class InboundConnectionWorkflowTests
{
    private FakeContactRequestCoordinator _contactCoordinator = null!;
    private FakeHandshakeService _handshakeService = null!;
    private InMemoryPendingHandshakeRepository _pendingHandshakeRepo = null!;
    private InMemoryChannelRepository _channelRepo = null!;
    private InMemoryPeerMailboxRoutingRepository _routingRepo = null!;
    private TestDateTimeProvider _timeProvider = null!;
    private FakeEventPublisher _eventPublisher = null!;
    private InboundConnectionWorkflow _sut = null!;

    private PublicIdentityId _personaId;
    private PublicIdentityId _requesterId;
    private RelayEndpoint _relayEndpoint = null!;

    [SetUp]
    public void SetUp()
    {
        _contactCoordinator = new FakeContactRequestCoordinator();
        _handshakeService = new FakeHandshakeService();
        _pendingHandshakeRepo = new InMemoryPendingHandshakeRepository();
        _channelRepo = new InMemoryChannelRepository();
        _routingRepo = new InMemoryPeerMailboxRoutingRepository();
        _timeProvider = new TestDateTimeProvider();
        _eventPublisher = new FakeEventPublisher();

        _personaId = PublicIdentityId.New();
        _requesterId = PublicIdentityId.New();
        _relayEndpoint = RelayEndpoint.Create("percolator://relay.example.com").Value;

        _routingRepo.SaveAsync(new PeerMailboxRoutingCapability(
            _requesterId,
            BlindedRoutingToken.New(),
            DeliveryToken.GenerateRandom(),
            _relayEndpoint,
            _timeProvider.UtcNow)).GetAwaiter().GetResult();

        _sut = new InboundConnectionWorkflow(
            _contactCoordinator,
            _handshakeService,
            _pendingHandshakeRepo,
            _channelRepo,
            _routingRepo,
            _timeProvider,
            _eventPublisher,
            NullLogger<InboundConnectionWorkflow>.Instance);
    }

    [Test]
    public void Constructor_WithNullDependencies_ThrowsArgumentNullException()
    {
        var logger = NullLogger<InboundConnectionWorkflow>.Instance;

        Action act1 = () => new InboundConnectionWorkflow(null!, _handshakeService, _pendingHandshakeRepo, _channelRepo, _routingRepo, _timeProvider, _eventPublisher, logger);
        Action act2 = () => new InboundConnectionWorkflow(_contactCoordinator, null!, _pendingHandshakeRepo, _channelRepo, _routingRepo, _timeProvider, _eventPublisher, logger);
        Action act3 = () => new InboundConnectionWorkflow(_contactCoordinator, _handshakeService, null!, _channelRepo, _routingRepo, _timeProvider, _eventPublisher, logger);
        Action act4 = () => new InboundConnectionWorkflow(_contactCoordinator, _handshakeService, _pendingHandshakeRepo, null!, _routingRepo, _timeProvider, _eventPublisher, logger);
        Action act5 = () => new InboundConnectionWorkflow(_contactCoordinator, _handshakeService, _pendingHandshakeRepo, _channelRepo, null!, _timeProvider, _eventPublisher, logger);
        Action act6 = () => new InboundConnectionWorkflow(_contactCoordinator, _handshakeService, _pendingHandshakeRepo, _channelRepo, _routingRepo, null!, _eventPublisher, logger);
        Action act7 = () => new InboundConnectionWorkflow(_contactCoordinator, _handshakeService, _pendingHandshakeRepo, _channelRepo, _routingRepo, _timeProvider, null!, logger);
        Action act8 = () => new InboundConnectionWorkflow(_contactCoordinator, _handshakeService, _pendingHandshakeRepo, _channelRepo, _routingRepo, _timeProvider, _eventPublisher, null!);

        act1.Should().Throw<ArgumentNullException>();
        act2.Should().Throw<ArgumentNullException>();
        act3.Should().Throw<ArgumentNullException>();
        act4.Should().Throw<ArgumentNullException>();
        act5.Should().Throw<ArgumentNullException>();
        act6.Should().Throw<ArgumentNullException>();
        act7.Should().Throw<ArgumentNullException>();
        act8.Should().Throw<ArgumentNullException>();
    }

    [Test]
    public async Task AcceptRequestAsync_WhenValid_ApprovesContactCompletesHandshakeAndCreatesDirectChannel()
    {
        var result = await _sut.AcceptRequestAsync(_personaId, _requesterId);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeTrue();

        _contactCoordinator.LastApproved.Should().NotBeNull();
        _contactCoordinator.LastApproved!.Value.PersonaId.Should().Be(_personaId);
        _contactCoordinator.LastApproved.Value.ContactId.Should().Be(_requesterId);
        _contactCoordinator.LastApproved.Value.TrustLevel.Should().Be(PeerTrustLevel.Tofu);

        _handshakeService.LastCompleted.Should().NotBeNull();
        _handshakeService.LastCompleted!.Value.RecipientId.Should().Be(_personaId);
        _handshakeService.LastCompleted.Value.SenderId.Should().Be(_requesterId);

        var expectedChannelId = Domain.Channels.ValueObjects.ChannelId.DeterministicForDirect(_personaId, _requesterId, _relayEndpoint);
        var createdChannel = await _channelRepo.GetDirectByIdAsync(expectedChannelId);
        createdChannel.Should().NotBeNull();
        createdChannel!.Status.Should().Be(Domain.Channels.Model.DirectChannelStatus.Active);
        createdChannel.OwnerIdentityId.Should().Be(_personaId);
        createdChannel.RemotePeerId.Should().Be(_requesterId);

        _eventPublisher.PublishedEvents.Should().ContainSingle(e => e is Percolator.Application.Channels.Events.DirectChannelCreatedEvent)
            .Which.Should().BeOfType<Percolator.Application.Channels.Events.DirectChannelCreatedEvent>()
            .Which.Should().Match<Percolator.Application.Channels.Events.DirectChannelCreatedEvent>(e =>
                e.ChannelId == expectedChannelId &&
                e.OwnerIdentityId == _personaId &&
                e.RemotePeerId == _requesterId &&
                e.Status == Domain.Channels.Model.DirectChannelStatus.Active);
    }

    [Test]
    public async Task AcceptRequestAsync_WhenApprovalFails_ReturnsFailureWithoutCompletingHandshake()
    {
        _contactCoordinator.ApproveResult = DomainResult.Failure(new DomainError("APPROVAL_ERROR", "Approval rejected"));

        var result = await _sut.AcceptRequestAsync(_personaId, _requesterId);

        result.IsSuccess.Should().BeFalse();
        result.Error.Code.Should().Be("APPROVAL_ERROR");
        _handshakeService.LastCompleted.Should().BeNull();
    }

    [Test]
    public async Task AcceptRequestAsync_WhenHandshakeCompletionFails_RevertsContactApprovalAndReturnsFailure()
    {
        _handshakeService.CompleteResult = DomainResult.Failure(new DomainError("CRYPTO_FAIL", "Key negotiation failed"));

        var result = await _sut.AcceptRequestAsync(_personaId, _requesterId);

        result.IsSuccess.Should().BeFalse();
        result.Error.Code.Should().Be("CRYPTO_FAIL");
        _contactCoordinator.LastApproved.Should().NotBeNull();
        _contactCoordinator.LastRejected.Should().NotBeNull();
        _contactCoordinator.LastRejected!.Value.PersonaId.Should().Be(_personaId);
        _contactCoordinator.LastRejected.Value.ContactId.Should().Be(_requesterId);
        _contactCoordinator.LastRejected.Value.Block.Should().BeFalse();
        _eventPublisher.PublishedEvents.Should().BeEmpty();
    }

    [Test]
    public async Task AcceptRequestAsync_WhenExistingChannelPresent_ActivatesAndPublishesEvent()
    {
        var expectedChannelId = Domain.Channels.ValueObjects.ChannelId.DeterministicForDirect(_personaId, _requesterId, _relayEndpoint);
        var existingChannel = Domain.Channels.Model.DirectChannel.Create(
            expectedChannelId,
            _personaId,
            _requesterId,
            _timeProvider,
            Domain.Channels.Model.DirectChannelStatus.PendingAcceptance,
            _relayEndpoint).Value!;
        existingChannel.ClearDomainEvents();
        await _channelRepo.SaveDirectAsync(existingChannel);

        var result = await _sut.AcceptRequestAsync(_personaId, _requesterId);

        result.IsSuccess.Should().BeTrue();
        var updatedChannel = await _channelRepo.GetDirectByIdAsync(expectedChannelId);
        updatedChannel!.Status.Should().Be(Domain.Channels.Model.DirectChannelStatus.Active);
        _eventPublisher.PublishedEvents.Should().ContainSingle(e => e is Percolator.Application.Channels.Events.DirectChannelActivatedEvent);
    }

    [Test]
    public async Task RejectRequestAsync_WhenValid_PurgesHandshakeAndRejectsContactWithoutBlock()
    {
        var envelope = new InboundHandshakeEnvelope(
            RecipientIdentityId: _personaId,
            SenderIdentityId: _requesterId,
            SenderDeviceId: DeviceId.Primary,
            SenderIdentityKey: IdentityKey.FromSpan(new byte[32]),
            SenderEphemeralKey: DhPublicKey.FromSpan(new byte[32]),
            SignedPreKeyId: 1,
            OneTimePreKeyId: null,
            EncryptedPayload: ReadOnlyMemory<byte>.Empty,
            ReceivedAtUtc: DateTimeOffset.UtcNow);

        await _pendingHandshakeRepo.SavePendingHandshakeAsync(_personaId, _requesterId, envelope);

        var result = await _sut.RejectRequestAsync(_personaId, _requesterId);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeTrue();

        (await _pendingHandshakeRepo.GetPendingHandshakeAsync(_personaId, _requesterId)).Should().BeNull();
        _contactCoordinator.LastRejected.Should().NotBeNull();
        _contactCoordinator.LastRejected!.Value.PersonaId.Should().Be(_personaId);
        _contactCoordinator.LastRejected.Value.ContactId.Should().Be(_requesterId);
        _contactCoordinator.LastRejected.Value.Block.Should().BeFalse();
    }

    [Test]
    public async Task BlockRequestAsync_WhenValid_PurgesHandshakeAndRejectsContactWithBlock()
    {
        var envelope = new InboundHandshakeEnvelope(
            RecipientIdentityId: _personaId,
            SenderIdentityId: _requesterId,
            SenderDeviceId: DeviceId.Primary,
            SenderIdentityKey: IdentityKey.FromSpan(new byte[32]),
            SenderEphemeralKey: DhPublicKey.FromSpan(new byte[32]),
            SignedPreKeyId: 1,
            OneTimePreKeyId: null,
            EncryptedPayload: ReadOnlyMemory<byte>.Empty,
            ReceivedAtUtc: DateTimeOffset.UtcNow);

        await _pendingHandshakeRepo.SavePendingHandshakeAsync(_personaId, _requesterId, envelope);

        var result = await _sut.BlockRequestAsync(_personaId, _requesterId);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeTrue();

        (await _pendingHandshakeRepo.GetPendingHandshakeAsync(_personaId, _requesterId)).Should().BeNull();
        _contactCoordinator.LastRejected.Should().NotBeNull();
        _contactCoordinator.LastRejected!.Value.PersonaId.Should().Be(_personaId);
        _contactCoordinator.LastRejected.Value.ContactId.Should().Be(_requesterId);
        _contactCoordinator.LastRejected.Value.Block.Should().BeTrue();
    }

    private sealed class FakeContactRequestCoordinator : IContactRequestCoordinator
    {
        public (PublicIdentityId PersonaId, PublicIdentityId ContactId, PeerTrustLevel TrustLevel)? LastApproved { get; private set; }
        public (PublicIdentityId PersonaId, PublicIdentityId ContactId, bool Block)? LastRejected { get; private set; }
        public DomainResult ApproveResult { get; set; } = DomainResult.Success();
        public DomainResult RejectResult { get; set; } = DomainResult.Success();

        public ValueTask<DomainResult> ApproveRequestAsync(PublicIdentityId personaId, PublicIdentityId contactId, PeerTrustLevel trustLevel, CancellationToken ct = default)
        {
            LastApproved = (personaId, contactId, trustLevel);
            return ValueTask.FromResult(ApproveResult);
        }

        public ValueTask<DomainResult> RejectRequestAsync(PublicIdentityId personaId, PublicIdentityId contactId, bool block, CancellationToken ct = default)
        {
            LastRejected = (personaId, contactId, block);
            return ValueTask.FromResult(RejectResult);
        }

        public ValueTask<DomainResult<PeerContact>> HandleInboundRequestAsync(PublicIdentityId ownerId, PublicIdentityId remotePeerId, IdentityKey peerPublicKey, string proposedNickname, CancellationToken ct = default)
            => throw new NotImplementedException();
    }

    private sealed class FakeHandshakeService : IHandshakeService
    {
        public (PublicIdentityId RecipientId, PublicIdentityId SenderId)? LastCompleted { get; private set; }
        public DomainResult CompleteResult { get; set; } = DomainResult.Success();

        public ValueTask<DomainResult> CompletePendingHandshakeAsync(PublicIdentityId recipientId, PublicIdentityId senderId, CancellationToken ct = default)
        {
            LastCompleted = (recipientId, senderId);
            return ValueTask.FromResult(CompleteResult);
        }

        public ValueTask<DomainResult<DirectRatchetSession>> InitiateHandshakeAsync(PublicIdentityId ownerId, DeviceId ownerDeviceId, PublicIdentityId remotePeerId, RelayEndpoint relayEndpoint, PreKeyBundle remoteBundle, AppId? initialAppId = null, ReadOnlyMemory<byte>? initialPayload = null, CancellationToken ct = default)
            => throw new NotImplementedException();

        public ValueTask<DomainResult> ReceiveInvitationAsync(InboundHandshakeEnvelope envelope, CancellationToken ct = default)
            => throw new NotImplementedException();
    }
}
