// SPECIFICATION GOVERNANCE: Read Percolator.Desktop/spec/ViewModel.spec.md before modifying this file.
// SPECIFICATION GOVERNANCE: Read Percolator.Desktop/spec/R3.spec.md before modifying this file.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Percolator.Application.Ingress;
using Percolator.Application.Ports;
using Percolator.Application.Profiles;
using Percolator.Desktop.Features.Transports.Models;
using Percolator.Desktop.Features.Transports.ViewModels;
using Percolator.Domain.Common;
using Percolator.Domain.Identities;
using Percolator.Domain.Identities.Model;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.Model;
using Percolator.PluginSdk;

namespace Percolator.Desktop.Tests.Features.Transports;

[TestFixture]
public sealed class PendingRequestsPanelViewModelTests
{
    private FakePeerContactQueryService _fakeQueryService = null!;
    private FakeHandshakeService _fakeHandshakeService = null!;
    private FakeContactRequestCoordinator _fakeContactCoordinator = null!;
    private FakePendingHandshakeRepository _fakePendingRepo = null!;
    private PublicIdentityId _ownerId;
    private PublicIdentityId _requesterId;

    [SetUp]
    public void SetUp()
    {
        _fakeQueryService = new FakePeerContactQueryService();
        _fakeHandshakeService = new FakeHandshakeService();
        _fakeContactCoordinator = new FakeContactRequestCoordinator();
        _fakePendingRepo = new FakePendingHandshakeRepository();
        _ownerId = PublicIdentityId.New();
        _requesterId = PublicIdentityId.New();
    }

    private PendingRequestsPanelViewModel CreateViewModel()
    {
        return new PendingRequestsPanelViewModel(
            _fakeQueryService,
            _fakeHandshakeService,
            _fakeContactCoordinator,
            _fakePendingRepo,
            NullLogger<PendingRequestsPanelViewModel>.Instance);
    }

    [Test]
    public void Constructor_WithNullDependencies_ThrowsArgumentNullException()
    {
        var logger = NullLogger<PendingRequestsPanelViewModel>.Instance;

        Action act1 = () => new PendingRequestsPanelViewModel(null!, _fakeHandshakeService, _fakeContactCoordinator, _fakePendingRepo, logger);
        Action act2 = () => new PendingRequestsPanelViewModel(_fakeQueryService, null!, _fakeContactCoordinator, _fakePendingRepo, logger);
        Action act3 = () => new PendingRequestsPanelViewModel(_fakeQueryService, _fakeHandshakeService, null!, _fakePendingRepo, logger);
        Action act4 = () => new PendingRequestsPanelViewModel(_fakeQueryService, _fakeHandshakeService, _fakeContactCoordinator, null!, logger);
        Action act5 = () => new PendingRequestsPanelViewModel(_fakeQueryService, _fakeHandshakeService, _fakeContactCoordinator, _fakePendingRepo, null!);

        act1.Should().Throw<ArgumentNullException>();
        act2.Should().Throw<ArgumentNullException>();
        act3.Should().Throw<ArgumentNullException>();
        act4.Should().Throw<ArgumentNullException>();
        act5.Should().Throw<ArgumentNullException>();
    }

    [Test]
    public async Task LoadRequestsAsync_PopulatesPendingRequests_FilteringPendingApprovalContacts()
    {
        using var vm = CreateViewModel();

        var pendingContact = new PeerContactSummaryReadModel(
            _requesterId, "Alice Pending", PeerTrustLevel.Untrusted, ContactState.PendingApproval, DateTimeOffset.UtcNow, false);

        var activeContact = new PeerContactSummaryReadModel(
            PublicIdentityId.New(), "Bob Active", PeerTrustLevel.Tofu, ContactState.Active, DateTimeOffset.UtcNow, true);

        var rejectedContact = new PeerContactSummaryReadModel(
            PublicIdentityId.New(), "Charlie Rejected", PeerTrustLevel.Blocked, ContactState.Rejected, DateTimeOffset.UtcNow, false);

        _fakeQueryService.Summaries.AddRange(new[] { pendingContact, activeContact, rejectedContact });

        await vm.InitializeAsync(_ownerId);

        vm.Requests.Should().ContainSingle();
        vm.Requests.First().RequesterId.Should().Be(_requesterId);
        vm.Requests.First().DisplayName.Should().Be("Alice Pending");
        vm.HasRequests.Should().BeTrue();
        vm.IsEmpty.Should().BeFalse();
        vm.RequestCount.Should().Be(1);
    }

    [Test]
    public async Task LoadRequestsAsync_FormatsFingerprintPreview_TruncatingHex()
    {
        using var vm = CreateViewModel();

        var pendingContact = new PeerContactSummaryReadModel(
            _requesterId, "Alice", PeerTrustLevel.Untrusted, ContactState.PendingApproval, DateTimeOffset.UtcNow, false);

        _fakeQueryService.Summaries.Add(pendingContact);

        await vm.InitializeAsync(_ownerId);

        var hex = _requesterId.Value.ToString("N");
        var expectedPreview = $"{hex[..4]}...{hex[^4..]}";

        vm.Requests.Should().ContainSingle();
        vm.Requests.First().FingerprintPreview.Should().Be(expectedPreview);
    }

    [Test]
    public async Task LoadRequestsAsync_WhenEmpty_SetsIsEmptyTrueAndHasRequestsFalse()
    {
        using var vm = CreateViewModel();

        await vm.InitializeAsync(_ownerId);

        vm.Requests.Should().BeEmpty();
        vm.HasRequests.Should().BeFalse();
        vm.IsEmpty.Should().BeTrue();
        vm.RequestCount.Should().Be(0);
    }

    [Test]
    public async Task AcceptRequestAsync_ApprovesContact_CompletesHandshake_AndRemovesFromList()
    {
        using var vm = CreateViewModel();

        var pendingContact = new PeerContactSummaryReadModel(
            _requesterId, "Alice", PeerTrustLevel.Untrusted, ContactState.PendingApproval, DateTimeOffset.UtcNow, false);

        _fakeQueryService.Summaries.Add(pendingContact);
        await vm.InitializeAsync(_ownerId);

        var item = vm.Requests.Single();
        await vm.AcceptRequestAsync(item);

        _fakeContactCoordinator.ApprovedRequests.Should().ContainSingle();
        _fakeContactCoordinator.ApprovedRequests[0].Should().Be((_ownerId, _requesterId, PeerTrustLevel.Tofu));

        _fakeHandshakeService.CompletedHandshakes.Should().ContainSingle();
        _fakeHandshakeService.CompletedHandshakes[0].Should().Be((_ownerId, _requesterId));

        vm.Requests.Should().BeEmpty();
        vm.HasRequests.Should().BeFalse();
        vm.StatusMessage.Should().Contain("Alice");
        vm.ErrorMessage.Should().BeNull();
    }

    [Test]
    public async Task AcceptRequestAsync_WhenApproveFails_SetsErrorMessageAndRetainsRequest()
    {
        using var vm = CreateViewModel();
        _fakeContactCoordinator.ApproveResult = DomainResult.Failure(new DomainError("DATABASE_ERROR", "DB lock failed"));

        var pendingContact = new PeerContactSummaryReadModel(
            _requesterId, "Alice", PeerTrustLevel.Untrusted, ContactState.PendingApproval, DateTimeOffset.UtcNow, false);

        _fakeQueryService.Summaries.Add(pendingContact);
        await vm.InitializeAsync(_ownerId);

        var item = vm.Requests.Single();
        await vm.AcceptRequestAsync(item);

        _fakeHandshakeService.CompletedHandshakes.Should().BeEmpty("handshake must not be completed if contact approval fails");
        vm.Requests.Should().ContainSingle("request must remain queued if approval fails");
        vm.ErrorMessage.Should().Contain("DB lock failed");
    }

    [Test]
    public async Task RejectRequestAsync_PurgesHandshake_RejectsContactWithoutBlock_AndRemovesFromList()
    {
        using var vm = CreateViewModel();

        var pendingContact = new PeerContactSummaryReadModel(
            _requesterId, "Bob", PeerTrustLevel.Untrusted, ContactState.PendingApproval, DateTimeOffset.UtcNow, false);

        _fakeQueryService.Summaries.Add(pendingContact);
        await vm.InitializeAsync(_ownerId);

        var item = vm.Requests.Single();
        await vm.RejectRequestAsync(item);

        _fakePendingRepo.DeletedHandshakes.Should().ContainSingle();
        _fakePendingRepo.DeletedHandshakes[0].Should().Be((_ownerId, _requesterId));

        _fakeContactCoordinator.RejectedRequests.Should().ContainSingle();
        _fakeContactCoordinator.RejectedRequests[0].Should().Be((_ownerId, _requesterId, false));

        vm.Requests.Should().BeEmpty();
        vm.HasRequests.Should().BeFalse();
        vm.StatusMessage.Should().Contain("Bob");
    }

    [Test]
    public async Task BlockRequestAsync_PurgesHandshake_RejectsContactWithBlock_AndRemovesFromList()
    {
        using var vm = CreateViewModel();

        var pendingContact = new PeerContactSummaryReadModel(
            _requesterId, "Eve Spammer", PeerTrustLevel.Untrusted, ContactState.PendingApproval, DateTimeOffset.UtcNow, false);

        _fakeQueryService.Summaries.Add(pendingContact);
        await vm.InitializeAsync(_ownerId);

        var item = vm.Requests.Single();
        await vm.BlockRequestAsync(item);

        _fakePendingRepo.DeletedHandshakes.Should().ContainSingle();
        _fakePendingRepo.DeletedHandshakes[0].Should().Be((_ownerId, _requesterId));

        _fakeContactCoordinator.RejectedRequests.Should().ContainSingle();
        _fakeContactCoordinator.RejectedRequests[0].Should().Be((_ownerId, _requesterId, true), "block flag must be true");

        vm.Requests.Should().BeEmpty();
        vm.HasRequests.Should().BeFalse();
        vm.StatusMessage.Should().Contain("Eve Spammer");
        vm.StatusMessage.Should().Contain("Future packets will be silently dropped");
    }
}

internal sealed class FakePeerContactQueryService : IPeerContactQueryService
{
    public List<PeerContactSummaryReadModel> Summaries { get; } = new();

    public Task<IReadOnlyList<PeerContactSummaryReadModel>> GetContactSummariesAsync(PublicIdentityId ownerId, CancellationToken ct = default)
    {
        return Task.FromResult<IReadOnlyList<PeerContactSummaryReadModel>>(Summaries.ToList());
    }

    public Task<PeerContactDetailReadModel?> GetContactDetailAsync(PublicIdentityId ownerId, PublicIdentityId contactId, CancellationToken ct = default)
    {
        return Task.FromResult<PeerContactDetailReadModel?>(null);
    }
}

internal sealed class FakeHandshakeService : IHandshakeService
{
    public DomainResult CompleteResult { get; set; } = DomainResult.Success();
    public List<(PublicIdentityId Recipient, PublicIdentityId Sender)> CompletedHandshakes { get; } = new();

    public ValueTask<DomainResult<DirectRatchetSession>> InitiateHandshakeAsync(
        PublicIdentityId ownerId,
        DeviceId ownerDeviceId,
        PublicIdentityId remotePeerId,
        PreKeyBundle remoteBundle,
        AppId? initialAppId = null,
        ReadOnlyMemory<byte>? initialPayload = null,
        CancellationToken ct = default) => throw new NotImplementedException();

    public ValueTask<DomainResult> ReceiveInvitationAsync(InboundHandshakeEnvelope envelope, CancellationToken ct = default) => throw new NotImplementedException();

    public ValueTask<DomainResult> CompletePendingHandshakeAsync(PublicIdentityId recipientId, PublicIdentityId senderId, CancellationToken ct = default)
    {
        CompletedHandshakes.Add((recipientId, senderId));
        return ValueTask.FromResult(CompleteResult);
    }
}

internal sealed class FakeContactRequestCoordinator : IContactRequestCoordinator
{
    public DomainResult ApproveResult { get; set; } = DomainResult.Success();
    public DomainResult RejectResult { get; set; } = DomainResult.Success();

    public List<(PublicIdentityId Owner, PublicIdentityId Peer, PeerTrustLevel Trust)> ApprovedRequests { get; } = new();
    public List<(PublicIdentityId Owner, PublicIdentityId Peer, bool Block)> RejectedRequests { get; } = new();

    public ValueTask<DomainResult<PeerContact>> HandleInboundRequestAsync(
        PublicIdentityId ownerId,
        PublicIdentityId remotePeerId,
        IdentityKey peerPublicKey,
        string proposedNickname,
        CancellationToken ct = default) => throw new NotImplementedException();

    public ValueTask<DomainResult> ApproveRequestAsync(
        PublicIdentityId ownerId,
        PublicIdentityId remotePeerId,
        PeerTrustLevel initialTrust = PeerTrustLevel.Tofu,
        CancellationToken ct = default)
    {
        ApprovedRequests.Add((ownerId, remotePeerId, initialTrust));
        return ValueTask.FromResult(ApproveResult);
    }

    public ValueTask<DomainResult> RejectRequestAsync(
        PublicIdentityId ownerId,
        PublicIdentityId remotePeerId,
        bool block = false,
        CancellationToken ct = default)
    {
        RejectedRequests.Add((ownerId, remotePeerId, block));
        return ValueTask.FromResult(RejectResult);
    }
}

internal sealed class FakePendingHandshakeRepository : IPendingHandshakeRepository
{
    public List<(PublicIdentityId Recipient, PublicIdentityId Sender)> DeletedHandshakes { get; } = new();

    public Task SavePendingHandshakeAsync(PublicIdentityId recipientId, PublicIdentityId senderId, InboundHandshakeEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;
    public Task<InboundHandshakeEnvelope?> GetPendingHandshakeAsync(PublicIdentityId recipientId, PublicIdentityId senderId, CancellationToken ct = default) => Task.FromResult<InboundHandshakeEnvelope?>(null);
    public Task DeletePendingHandshakeAsync(PublicIdentityId recipientId, PublicIdentityId senderId, CancellationToken ct = default)
    {
        DeletedHandshakes.Add((recipientId, senderId));
        return Task.CompletedTask;
    }
}
