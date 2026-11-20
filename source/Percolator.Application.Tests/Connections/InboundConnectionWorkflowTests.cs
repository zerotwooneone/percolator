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
using Percolator.Domain.Security.Model;
using Percolator.PluginSdk;

[TestFixture]
public sealed class InboundConnectionWorkflowTests
{
    private FakeContactRequestCoordinator _contactCoordinator = null!;
    private FakeHandshakeService _handshakeService = null!;
    private InMemoryPendingHandshakeRepository _pendingHandshakeRepo = null!;
    private InboundConnectionWorkflow _sut = null!;

    private PublicIdentityId _personaId;
    private PublicIdentityId _requesterId;

    [SetUp]
    public void SetUp()
    {
        _contactCoordinator = new FakeContactRequestCoordinator();
        _handshakeService = new FakeHandshakeService();
        _pendingHandshakeRepo = new InMemoryPendingHandshakeRepository();

        _personaId = PublicIdentityId.New();
        _requesterId = PublicIdentityId.New();

        _sut = new InboundConnectionWorkflow(
            _contactCoordinator,
            _handshakeService,
            _pendingHandshakeRepo,
            NullLogger<InboundConnectionWorkflow>.Instance);
    }

    [Test]
    public void Constructor_WithNullDependencies_ThrowsArgumentNullException()
    {
        var logger = NullLogger<InboundConnectionWorkflow>.Instance;

        Action act1 = () => new InboundConnectionWorkflow(null!, _handshakeService, _pendingHandshakeRepo, logger);
        Action act2 = () => new InboundConnectionWorkflow(_contactCoordinator, null!, _pendingHandshakeRepo, logger);
        Action act3 = () => new InboundConnectionWorkflow(_contactCoordinator, _handshakeService, null!, logger);
        Action act4 = () => new InboundConnectionWorkflow(_contactCoordinator, _handshakeService, _pendingHandshakeRepo, null!);

        act1.Should().Throw<ArgumentNullException>();
        act2.Should().Throw<ArgumentNullException>();
        act3.Should().Throw<ArgumentNullException>();
        act4.Should().Throw<ArgumentNullException>();
    }

    [Test]
    public async Task AcceptRequestAsync_WhenValid_ApprovesContactAndCompletesHandshake()
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

        public ValueTask<DomainResult<DirectRatchetSession>> InitiateHandshakeAsync(PublicIdentityId ownerId, DeviceId ownerDeviceId, PublicIdentityId remotePeerId, PreKeyBundle remoteBundle, AppId? initialAppId = null, ReadOnlyMemory<byte>? initialPayload = null, CancellationToken ct = default)
            => throw new NotImplementedException();

        public ValueTask<DomainResult> ReceiveInvitationAsync(InboundHandshakeEnvelope envelope, CancellationToken ct = default)
            => throw new NotImplementedException();
    }
}
