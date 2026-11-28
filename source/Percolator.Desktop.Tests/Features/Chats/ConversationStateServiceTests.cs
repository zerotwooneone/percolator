// SPECIFICATION GOVERNANCE: Read Percolator.Desktop/spec/UiStateService.spec.md before modifying this file.

namespace Percolator.Desktop.Tests.Features.Chats;

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
using Percolator.Application.Channels.Events;
using Percolator.Application.Invitations;
using Percolator.Application.Ports;
using Percolator.Apps.Chat.Features.Messaging;
using Percolator.Desktop.Common.Services;
using Percolator.Desktop.Features.Chats.Models;
using Percolator.Desktop.Features.Chats.Services;
using Percolator.Desktop.Features.Shell.Models;
using Percolator.Desktop.Features.Transports.Models;
using Percolator.Desktop.Tests.Features.Transports;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities;
using Percolator.Domain.Identities.Model;
using Percolator.Domain.Identities.ValueObjects;
using R3;

[TestFixture]
public sealed class ConversationStateServiceTests
{
    private Mock<IConversationListQueryService> _conversationQueryMock = null!;
    private Mock<IPeerContactQueryService> _contactQueryMock = null!;
    private FakeOutboundPendingRetryWorker _fakeRetryWorker = null!;
    private Mock<ICurrentPersonaService> _currentPersonaMock = null!;
    private Subject<ActivePersonaModel?> _currentPersonaChanged = null!;
    private Percolator.Infrastructure.Events.InMemoryEventBus _eventBus = null!;
    private FakeTimeProvider _timeProvider = null!;

    private PublicIdentityId _personaId;
    private ActivePersonaModel _personaModel = null!;
    private PublicIdentityId _peerId;
    private OutboundPendingConnectionModel _pendingModel = null!;

    [SetUp]
    public void SetUp()
    {
        _conversationQueryMock = new Mock<IConversationListQueryService>();
        _contactQueryMock = new Mock<IPeerContactQueryService>();
        _fakeRetryWorker = new FakeOutboundPendingRetryWorker();
        _currentPersonaMock = new Mock<ICurrentPersonaService>();
        _currentPersonaChanged = new Subject<ActivePersonaModel?>();
        _eventBus = new Percolator.Infrastructure.Events.InMemoryEventBus(NullLogger<Percolator.Infrastructure.Events.InMemoryEventBus>.Instance);
        _timeProvider = new FakeTimeProvider(DateTimeOffset.UtcNow);

        _personaId = PublicIdentityId.New();
        _personaModel = new ActivePersonaModel(_personaId, "Alice", 5124);
        _peerId = PublicIdentityId.New();

        _currentPersonaMock.SetupGet(p => p.CurrentPersona).Returns(_personaModel);
        _currentPersonaMock.SetupGet(p => p.CurrentPersonaChanged).Returns(_currentPersonaChanged);

        _conversationQueryMock
            .Setup(q => q.GetConversationsAsync(It.IsAny<PublicIdentityId>(), It.IsAny<ulong>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ConversationSummaryReadModel>());

        _contactQueryMock
            .Setup(q => q.GetContactSummariesAsync(It.IsAny<PublicIdentityId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PeerContactSummaryReadModel>());

        var payload = new PeerInvitationPayload(
            _peerId,
            IdentityKey.FromBytes(new byte[32]),
            RelayEndpoint.Parse("127.0.0.1:5100"),
            BlindedRoutingToken.New());

        _pendingModel = new OutboundPendingConnectionModel(
            _personaId,
            _peerId,
            "Bob Offline",
            payload,
            _timeProvider.GetUtcNow());
    }

    [TearDown]
    public void TearDown()
    {
        _currentPersonaChanged.Dispose();
        _fakeRetryWorker.Dispose();
        _eventBus.Dispose();
    }

    private ConversationStateService CreateService()
    {
        return new ConversationStateService(
            _conversationQueryMock.Object,
            _contactQueryMock.Object,
            _fakeRetryWorker,
            _currentPersonaMock.Object,
            _eventBus,
            NullLogger<ConversationStateService>.Instance,
            _timeProvider);
    }

    [Test]
    public void Constructor_WithNullDependencies_ThrowsArgumentNullException()
    {
        var logger = NullLogger<ConversationStateService>.Instance;

        Action act1 = () => new ConversationStateService(null!, _contactQueryMock.Object, _fakeRetryWorker, _currentPersonaMock.Object, _eventBus, logger, _timeProvider);
        Action act2 = () => new ConversationStateService(_conversationQueryMock.Object, null!, _fakeRetryWorker, _currentPersonaMock.Object, _eventBus, logger, _timeProvider);
        Action act3 = () => new ConversationStateService(_conversationQueryMock.Object, _contactQueryMock.Object, null!, _currentPersonaMock.Object, _eventBus, logger, _timeProvider);
        Action act4 = () => new ConversationStateService(_conversationQueryMock.Object, _contactQueryMock.Object, _fakeRetryWorker, null!, _eventBus, logger, _timeProvider);
        Action act5 = () => new ConversationStateService(_conversationQueryMock.Object, _contactQueryMock.Object, _fakeRetryWorker, _currentPersonaMock.Object, null!, logger, _timeProvider);
        Action act6 = () => new ConversationStateService(_conversationQueryMock.Object, _contactQueryMock.Object, _fakeRetryWorker, _currentPersonaMock.Object, _eventBus, null!, _timeProvider);
        Action act7 = () => new ConversationStateService(_conversationQueryMock.Object, _contactQueryMock.Object, _fakeRetryWorker, _currentPersonaMock.Object, _eventBus, logger, null!);

        act1.Should().Throw<ArgumentNullException>();
        act2.Should().Throw<ArgumentNullException>();
        act3.Should().Throw<ArgumentNullException>();
        act4.Should().Throw<ArgumentNullException>();
        act5.Should().Throw<ArgumentNullException>();
        act6.Should().Throw<ArgumentNullException>();
        act7.Should().Throw<ArgumentNullException>();
    }

    [Test]
    public async Task LoadConversationsForPersonaAsync_PopulatesConversationsFromQueryServices()
    {
        var channelId = ChannelId.New();
        var convRead = new ConversationSummaryReadModel(
            channelId,
            ConversationChannelKind.Direct,
            _peerId,
            "Bob Chat",
            "Hey Alice",
            (ulong)_timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
            2,
            _peerId,
            IsPendingAcceptance: false,
            RelayEndpoint: null,
            RelayIdentityId: null);

        _conversationQueryMock
            .Setup(q => q.GetConversationsAsync(_personaId, It.IsAny<ulong>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ConversationSummaryReadModel> { convRead });

        using var service = CreateService();
        await service.LoadConversationsForPersonaAsync(_personaId);

        service.Conversations.Should().ContainSingle();
        var conv = service.Conversations.First();
        conv.ConversationId.Should().Be(channelId);
        conv.DisplayName.Should().Be("Bob Chat");
        conv.LastMessageSnippet.Should().Be("Hey Alice");
        conv.UnreadCount.Should().Be(2);
        conv.IsPendingOutbound.Should().BeFalse();
        conv.DeliveryStatus.Should().Be(ConversationDeliveryStatus.Connected);
    }

    [Test]
    public async Task LoadConversationsForPersonaAsync_WhenPendingAcceptance_MapsPendingDeliveryStatus()
    {
        var channelId = ChannelId.New();
        var convRead = new ConversationSummaryReadModel(
            channelId,
            ConversationChannelKind.Direct,
            _peerId,
            "Bob Chat",
            "Handshake request",
            (ulong)_timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
            0,
            _peerId,
            IsPendingAcceptance: true,
            RelayEndpoint: null,
            RelayIdentityId: null);

        _conversationQueryMock
            .Setup(q => q.GetConversationsAsync(_personaId, It.IsAny<ulong>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ConversationSummaryReadModel> { convRead });

        using var service = CreateService();
        await service.LoadConversationsForPersonaAsync(_personaId);

        service.Conversations.Should().ContainSingle();
        var conv = service.Conversations.First();
        conv.DeliveryStatus.Should().Be(ConversationDeliveryStatus.Pending);
    }

    [Test]
    public void StagePendingOutbound_AddsConversationWithPendingStatus_AndEnqueuesRetry()
    {
        using var service = CreateService();

        service.StagePendingOutbound(_pendingModel);

        service.Conversations.Should().ContainSingle();
        var conv = service.Conversations.First();
        conv.PeerOrGroupId.Should().Be(_peerId);
        conv.DisplayName.Should().Be("Bob Offline");
        conv.IsPendingOutbound.Should().BeTrue();
        conv.DeliveryStatus.Should().Be(ConversationDeliveryStatus.Pending);

        _fakeRetryWorker.EnqueuedRetries.Should().ContainSingle(r => r.PeerId == _peerId);
        service.IsPending(_peerId).Should().BeTrue();
    }

    [Test]
    public void CancelOutboundRequest_RemovesPendingItem_AndAbortsRetry()
    {
        using var service = CreateService();
        service.StagePendingOutbound(_pendingModel);

        PublicIdentityId? cancelledPeer = null;
        using var sub = service.ConnectionCancelled.Subscribe(id => cancelledPeer = id);

        service.CancelOutboundRequest(_peerId);

        service.Conversations.Should().BeEmpty();
        service.IsPending(_peerId).Should().BeFalse();
        _fakeRetryWorker.CancelledRetries.Should().Contain(_peerId);
        cancelledPeer.Should().Be(_peerId);
    }

    [Test]
    public void ConnectionSucceeded_FromRetryWorker_TransitionsConversationToConnected()
    {
        using var service = CreateService();
        service.StagePendingOutbound(_pendingModel);

        OutboundPendingConnectionModel? succeededModel = null;
        using var sub = service.ConnectionSucceeded.Subscribe(m => succeededModel = m);

        _fakeRetryWorker.SimulateSuccess(_pendingModel);

        service.Conversations.Should().ContainSingle();
        var conv = service.Conversations.First();
        conv.IsPendingOutbound.Should().BeFalse();
        conv.DeliveryStatus.Should().Be(ConversationDeliveryStatus.Connected);
        conv.LastMessageSnippet.Should().Be("Connected.");

        succeededModel.Should().Be(_pendingModel);
    }

    [Test]
    public void RegisterDirectConversation_AddsNewConversationWhenNotPresent()
    {
        using var service = CreateService();
        var directPeer = PublicIdentityId.New();

        service.RegisterDirectConversation(directPeer, "Charlie Direct");

        service.Conversations.Should().ContainSingle(c => c.PeerOrGroupId == directPeer);
        var conv = service.Conversations.First(c => c.PeerOrGroupId == directPeer);
        conv.DisplayName.Should().Be("Charlie Direct");
        conv.RouteType.Should().Be(ConversationRouteType.DirectP2P);
        conv.DeliveryStatus.Should().Be(ConversationDeliveryStatus.Connected);
    }

    [Test]
    public async Task EventSubscriber_DirectChannelCreatedEvent_TriggersReload()
    {
        using var service = CreateService();

        var evt = new Percolator.Application.Channels.Events.DirectChannelCreatedEvent
        {
            ChannelId = ChannelId.New(),
            OwnerIdentityId = _personaId,
            RemotePeerId = PublicIdentityId.New(),
            Status = Domain.Channels.Model.DirectChannelStatus.Active
        };

        await _eventBus.PublishAsync(evt);

        _conversationQueryMock.Verify(
            q => q.GetConversationsAsync(_personaId, It.IsAny<ulong>(), It.IsAny<CancellationToken>()),
            Times.AtLeast(2));
    }

    [Test]
    public async Task EventSubscriber_BurstOfDirectChannelCreatedEvents_CoalescesViaThrottleFirstLast()
    {
        using var service = CreateService();
        // Initial constructor load triggers 1 call.
        _conversationQueryMock.Verify(
            q => q.GetConversationsAsync(_personaId, It.IsAny<ulong>(), It.IsAny<CancellationToken>()),
            Times.Once);

        for (int i = 0; i < 5; i++)
        {
            await _eventBus.PublishAsync(new DirectChannelCreatedEvent
            {
                ChannelId = ChannelId.New(),
                OwnerIdentityId = _personaId,
                RemotePeerId = PublicIdentityId.New(),
                Status = Domain.Channels.Model.DirectChannelStatus.Active
            });
            _timeProvider.Advance(TimeSpan.FromMilliseconds(5));
        }

        // Leading edge fired once; subsequent burst events are coalesced during 50ms window.
        _conversationQueryMock.Verify(
            q => q.GetConversationsAsync(_personaId, It.IsAny<ulong>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2));

        // Advancing past 50ms window triggers the single coalesced trailing reload.
        _timeProvider.Advance(TimeSpan.FromMilliseconds(35));

        _conversationQueryMock.Verify(
            q => q.GetConversationsAsync(_personaId, It.IsAny<ulong>(), It.IsAny<CancellationToken>()),
            Times.Exactly(3));
    }

    [Test]
    public async Task EventSubscriber_SlowInFlightReload_IsCancelledByTrailingSwitchAndPersonaLock()
    {
        var callCount = 0;
        var secondCallTcs = new TaskCompletionSource<IReadOnlyList<ConversationSummaryReadModel>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thirdCallTcs = new TaskCompletionSource<IReadOnlyList<ConversationSummaryReadModel>>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken secondCallToken = default;
        CancellationToken thirdCallToken = default;

        _conversationQueryMock
            .Setup(q => q.GetConversationsAsync(_personaId, It.IsAny<ulong>(), It.IsAny<CancellationToken>()))
            .Returns((PublicIdentityId _, ulong _, CancellationToken ct) =>
            {
                callCount++;
                if (callCount == 1)
                {
                    return Task.FromResult<IReadOnlyList<ConversationSummaryReadModel>>(Array.Empty<ConversationSummaryReadModel>());
                }

                if (callCount == 2)
                {
                    secondCallToken = ct;
                    return secondCallTcs.Task;
                }

                thirdCallToken = ct;
                return thirdCallTcs.Task;
            });

        using var service = CreateService();

        // Event 1: triggers leading edge reload (call #2, in-flight).
        await _eventBus.PublishAsync(new DirectChannelCreatedEvent
        {
            ChannelId = ChannelId.New(),
            OwnerIdentityId = _personaId,
            RemotePeerId = PublicIdentityId.New(),
            Status = Domain.Channels.Model.DirectChannelStatus.Active
        });

        secondCallToken.CanBeCanceled.Should().BeTrue();
        secondCallToken.IsCancellationRequested.Should().BeFalse();

        // Event 2 inside 50ms window queues trailing reload.
        _timeProvider.Advance(TimeSpan.FromMilliseconds(10));
        await _eventBus.PublishAsync(new DirectChannelCreatedEvent
        {
            ChannelId = ChannelId.New(),
            OwnerIdentityId = _personaId,
            RemotePeerId = PublicIdentityId.New(),
            Status = Domain.Channels.Model.DirectChannelStatus.Active
        });

        // Advancing past 50ms fires trailing emission; AwaitOperation.Switch cancels call #2 and starts call #3.
        _timeProvider.Advance(TimeSpan.FromMilliseconds(50));
        secondCallToken.IsCancellationRequested.Should().BeTrue();
        thirdCallToken.IsCancellationRequested.Should().BeFalse();

        // Locking vault cancels call #3 and prevents stale completion from populating state.
        _currentPersonaChanged.OnNext(null);
        thirdCallToken.IsCancellationRequested.Should().BeTrue();

        thirdCallTcs.SetResult(new[]
        {
            new ConversationSummaryReadModel(
                ChannelId.New(),
                ConversationChannelKind.Direct,
                _peerId,
                "Stale Chat",
                "Should never appear",
                (ulong)_timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
                1,
                _peerId,
                IsPendingAcceptance: false,
                RelayEndpoint: null,
                RelayIdentityId: null)
        });

        service.Conversations.Should().BeEmpty();
    }
}
