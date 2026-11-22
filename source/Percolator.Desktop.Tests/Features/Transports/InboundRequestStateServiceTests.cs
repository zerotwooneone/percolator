// SPECIFICATION GOVERNANCE: Read Percolator.Desktop/spec/UiStateService.spec.md before modifying this file.

namespace Percolator.Desktop.Tests.Features.Transports;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using NUnit.Framework;
using Percolator.Application.Connections;
using Percolator.Application.Ports;
using Percolator.Application.Profiles.Events;
using Percolator.Desktop.Common.Services;
using Percolator.Desktop.Features.Shell.Models;
using Percolator.Desktop.Features.Transports.Services;
using Percolator.Domain.Common;
using Percolator.Domain.Identities;
using Percolator.Domain.Identities.Model;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.ValueObjects;
using R3;

[TestFixture]
public sealed class InboundRequestStateServiceTests
{
    private Mock<IPeerContactQueryService> _queryServiceMock = null!;
    private Mock<IInboundConnectionWorkflow> _workflowMock = null!;
    private Mock<ICurrentPersonaService> _currentPersonaMock = null!;
    private Subject<ActivePersonaModel?> _currentPersonaChanged = null!;
    private Percolator.Infrastructure.Events.InMemoryEventBus _eventBus = null!;
    private FakeTimeProvider _timeProvider = null!;
    private PublicIdentityId _personaId;
    private ActivePersonaModel _personaModel = null!;

    [SetUp]
    public void SetUp()
    {
        _queryServiceMock = new Mock<IPeerContactQueryService>();
        _workflowMock = new Mock<IInboundConnectionWorkflow>();
        _currentPersonaMock = new Mock<ICurrentPersonaService>();
        _currentPersonaChanged = new Subject<ActivePersonaModel?>();
        _eventBus = new Percolator.Infrastructure.Events.InMemoryEventBus(NullLogger<Percolator.Infrastructure.Events.InMemoryEventBus>.Instance);
        _timeProvider = new FakeTimeProvider(DateTimeOffset.UtcNow);

        _personaId = PublicIdentityId.New();
        _personaModel = new ActivePersonaModel(_personaId, "Alice", 5124);

        _currentPersonaMock.SetupGet(p => p.CurrentPersona).Returns(_personaModel);
        _currentPersonaMock.SetupGet(p => p.CurrentPersonaChanged).Returns(_currentPersonaChanged);

        _workflowMock
            .Setup(w => w.AcceptRequestAsync(It.IsAny<PublicIdentityId>(), It.IsAny<PublicIdentityId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DomainResult<bool>.Success(true));

        _workflowMock
            .Setup(w => w.RejectRequestAsync(It.IsAny<PublicIdentityId>(), It.IsAny<PublicIdentityId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DomainResult<bool>.Success(true));

        _workflowMock
            .Setup(w => w.BlockRequestAsync(It.IsAny<PublicIdentityId>(), It.IsAny<PublicIdentityId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DomainResult<bool>.Success(true));
    }

    [TearDown]
    public void TearDown()
    {
        _currentPersonaChanged.Dispose();
        _eventBus.Dispose();
    }

    private InboundRequestStateService CreateService()
    {
        return new InboundRequestStateService(
            _queryServiceMock.Object,
            _workflowMock.Object,
            _currentPersonaMock.Object,
            _eventBus,
            NullLogger<InboundRequestStateService>.Instance,
            _timeProvider);
    }

    [Test]
    public void Constructor_WithNullDependencies_ThrowsArgumentNullException()
    {
        var logger = NullLogger<InboundRequestStateService>.Instance;

        Action act1 = () => new InboundRequestStateService(null!, _workflowMock.Object, _currentPersonaMock.Object, _eventBus, logger, _timeProvider);
        Action act2 = () => new InboundRequestStateService(_queryServiceMock.Object, null!, _currentPersonaMock.Object, _eventBus, logger, _timeProvider);
        Action act3 = () => new InboundRequestStateService(_queryServiceMock.Object, _workflowMock.Object, null!, _eventBus, logger, _timeProvider);
        Action act4 = () => new InboundRequestStateService(_queryServiceMock.Object, _workflowMock.Object, _currentPersonaMock.Object, null!, logger, _timeProvider);
        Action act5 = () => new InboundRequestStateService(_queryServiceMock.Object, _workflowMock.Object, _currentPersonaMock.Object, _eventBus, null!, _timeProvider);
        Action act6 = () => new InboundRequestStateService(_queryServiceMock.Object, _workflowMock.Object, _currentPersonaMock.Object, _eventBus, logger, null!);

        act1.Should().Throw<ArgumentNullException>();
        act2.Should().Throw<ArgumentNullException>();
        act3.Should().Throw<ArgumentNullException>();
        act4.Should().Throw<ArgumentNullException>();
        act5.Should().Throw<ArgumentNullException>();
        act6.Should().Throw<ArgumentNullException>();
    }

    [Test]
    public async Task LoadRequestsForPersonaAsync_PopulatesRequestsAndUpdatesCount()
    {
        var requesterId = PublicIdentityId.New();
        var pendingSummary = new PeerContactSummaryReadModel(
            requesterId, "Bob Initiator", PeerTrustLevel.Untrusted, ContactState.PendingApproval, DateTimeOffset.UtcNow, false);

        _queryServiceMock
            .Setup(q => q.GetContactSummariesAsync(_personaId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PeerContactSummaryReadModel> { pendingSummary });

        using var service = CreateService();
        await service.LoadRequestsForPersonaAsync(_personaId);

        service.Requests.Should().ContainSingle();
        service.Requests.First().RequesterId.Should().Be(requesterId);
        service.Requests.First().DisplayName.Should().Be("Bob Initiator");
        service.PendingCount.CurrentValue.Should().Be(1);
    }

    [Test]
    public async Task AcceptRequestAsync_RemovesItemFromQueueAndDecrementsCount()
    {
        var requesterId = PublicIdentityId.New();
        var pendingSummary = new PeerContactSummaryReadModel(
            requesterId, "Bob", PeerTrustLevel.Untrusted, ContactState.PendingApproval, DateTimeOffset.UtcNow, false);

        _queryServiceMock
            .Setup(q => q.GetContactSummariesAsync(_personaId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PeerContactSummaryReadModel> { pendingSummary });

        using var service = CreateService();
        await service.LoadRequestsForPersonaAsync(_personaId);

        service.Requests.Should().ContainSingle();
        service.PendingCount.CurrentValue.Should().Be(1);

        await service.AcceptRequestAsync(_personaId, requesterId);

        service.Requests.Should().BeEmpty();
        service.PendingCount.CurrentValue.Should().Be(0);
    }

    [Test]
    public async Task RejectRequestAsync_RemovesItemFromQueueAndDecrementsCount()
    {
        var requesterId = PublicIdentityId.New();
        var pendingSummary = new PeerContactSummaryReadModel(
            requesterId, "Charlie", PeerTrustLevel.Untrusted, ContactState.PendingApproval, DateTimeOffset.UtcNow, false);

        _queryServiceMock
            .Setup(q => q.GetContactSummariesAsync(_personaId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PeerContactSummaryReadModel> { pendingSummary });

        using var service = CreateService();
        await service.LoadRequestsForPersonaAsync(_personaId);

        service.Requests.Should().ContainSingle();
        service.PendingCount.CurrentValue.Should().Be(1);

        await service.RejectRequestAsync(_personaId, requesterId);

        service.Requests.Should().BeEmpty();
        service.PendingCount.CurrentValue.Should().Be(0);
    }

    [Test]
    public async Task BlockRequestAsync_RemovesItemFromQueueAndDecrementsCount()
    {
        var requesterId = PublicIdentityId.New();
        var pendingSummary = new PeerContactSummaryReadModel(
            requesterId, "Eve Spammer", PeerTrustLevel.Untrusted, ContactState.PendingApproval, DateTimeOffset.UtcNow, false);

        _queryServiceMock
            .Setup(q => q.GetContactSummariesAsync(_personaId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PeerContactSummaryReadModel> { pendingSummary });

        using var service = CreateService();
        await service.LoadRequestsForPersonaAsync(_personaId);

        service.Requests.Should().ContainSingle();
        service.PendingCount.CurrentValue.Should().Be(1);

        await service.BlockRequestAsync(_personaId, requesterId);

        service.Requests.Should().BeEmpty();
        service.PendingCount.CurrentValue.Should().Be(0);
    }

    [Test]
    public async Task EventSubscriber_InboundContactRequestReceivedEvent_TriggersReload()
    {
        var requesterId = PublicIdentityId.New();
        var pendingSummary = new PeerContactSummaryReadModel(
            requesterId, "Bob", PeerTrustLevel.Untrusted, ContactState.PendingApproval, DateTimeOffset.UtcNow, false);

        _queryServiceMock
            .Setup(q => q.GetContactSummariesAsync(_personaId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PeerContactSummaryReadModel> { pendingSummary });

        using var service = CreateService();

        var evt = new Percolator.Application.Profiles.Events.InboundContactRequestReceivedEvent
        {
            OwnerIdentityId = _personaId,
            RemotePeerId = requesterId,
            PrimaryPublicKey = IdentityKey.FromSpan(new byte[32])
        };

        await _eventBus.PublishAsync(evt);

        service.Requests.Should().ContainSingle();
        service.Requests.First().RequesterId.Should().Be(requesterId);
        service.PendingCount.CurrentValue.Should().Be(1);
    }

    [Test]
    public async Task EventSubscriber_ContactRequestApprovedEvent_TriggersReload()
    {
        _queryServiceMock
            .Setup(q => q.GetContactSummariesAsync(_personaId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PeerContactSummaryReadModel>());

        using var service = CreateService();

        var evt = new Percolator.Application.Profiles.Events.ContactRequestApprovedEvent
        {
            OwnerIdentityId = _personaId,
            RemotePeerId = PublicIdentityId.New(),
            TrustLevel = PeerTrustLevel.Tofu
        };

        await _eventBus.PublishAsync(evt);

        _queryServiceMock.Verify(q => q.GetContactSummariesAsync(_personaId, It.IsAny<CancellationToken>()), Times.AtLeast(2));
    }

    [Test]
    public async Task EventSubscriber_BurstOfInboundRequests_CoalescesViaThrottleFirstLast()
    {
        _queryServiceMock
            .Setup(q => q.GetContactSummariesAsync(_personaId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PeerContactSummaryReadModel>());

        using var service = CreateService();
        // Initial constructor load triggers 1 call.
        _queryServiceMock.Verify(q => q.GetContactSummariesAsync(_personaId, It.IsAny<CancellationToken>()), Times.Once);

        for (int i = 0; i < 5; i++)
        {
            await _eventBus.PublishAsync(new InboundContactRequestReceivedEvent
            {
                OwnerIdentityId = _personaId,
                RemotePeerId = PublicIdentityId.New(),
                PrimaryPublicKey = IdentityKey.FromSpan(new byte[32])
            });
            _timeProvider.Advance(TimeSpan.FromMilliseconds(5));
        }

        // Leading edge executed immediately; remaining burst events are coalesced during 50ms window.
        _queryServiceMock.Verify(q => q.GetContactSummariesAsync(_personaId, It.IsAny<CancellationToken>()), Times.Exactly(2));

        // Advancing past the 50ms ThrottleFirstLast window fires the single coalesced trailing reload.
        _timeProvider.Advance(TimeSpan.FromMilliseconds(35));

        _queryServiceMock.Verify(q => q.GetContactSummariesAsync(_personaId, It.IsAny<CancellationToken>()), Times.Exactly(3));
    }

    [Test]
    public async Task PersonaLock_WhileReloadInFlight_CancelsQueryAndPreventsStaleOverwrite()
    {
        var callCount = 0;
        var inFlightTcs = new TaskCompletionSource<IReadOnlyList<PeerContactSummaryReadModel>>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken capturedToken = default;

        _queryServiceMock
            .Setup(q => q.GetContactSummariesAsync(_personaId, It.IsAny<CancellationToken>()))
            .Returns((PublicIdentityId _, CancellationToken ct) =>
            {
                callCount++;
                if (callCount == 1)
                {
                    return Task.FromResult<IReadOnlyList<PeerContactSummaryReadModel>>(Array.Empty<PeerContactSummaryReadModel>());
                }

                capturedToken = ct;
                return inFlightTcs.Task;
            });

        using var service = CreateService();

        await _eventBus.PublishAsync(new InboundContactRequestReceivedEvent
        {
            OwnerIdentityId = _personaId,
            RemotePeerId = PublicIdentityId.New(),
            PrimaryPublicKey = IdentityKey.FromSpan(new byte[32])
        });

        capturedToken.CanBeCanceled.Should().BeTrue();
        capturedToken.IsCancellationRequested.Should().BeFalse();

        // Locking vault clears active persona and cancels any in-flight reload token.
        _currentPersonaChanged.OnNext(null);
        capturedToken.IsCancellationRequested.Should().BeTrue();

        // Even if an uncooperative query finishes after cancellation, stale data must not overwrite cleared state.
        inFlightTcs.SetResult(new[]
        {
            new PeerContactSummaryReadModel(
                PublicIdentityId.New(), "Stale Peer", PeerTrustLevel.Untrusted, ContactState.PendingApproval, _timeProvider.GetUtcNow(), false)
        });

        service.Requests.Should().BeEmpty();
        service.PendingCount.CurrentValue.Should().Be(0);
    }
}
