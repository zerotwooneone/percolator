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
            BlindedRoutingToken.New(),
            DeliveryToken.GenerateRandom(),
            new[] { "127.0.0.1" },
            PortRange.Single(5100));

        _pendingModel = new OutboundPendingConnectionModel(
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
    }

    private ConversationStateService CreateService()
    {
        return new ConversationStateService(
            _conversationQueryMock.Object,
            _contactQueryMock.Object,
            _fakeRetryWorker,
            _currentPersonaMock.Object,
            NullLogger<ConversationStateService>.Instance,
            _timeProvider);
    }

    [Test]
    public void Constructor_WithNullDependencies_ThrowsArgumentNullException()
    {
        var logger = NullLogger<ConversationStateService>.Instance;

        Action act1 = () => new ConversationStateService(null!, _contactQueryMock.Object, _fakeRetryWorker, _currentPersonaMock.Object, logger, _timeProvider);
        Action act2 = () => new ConversationStateService(_conversationQueryMock.Object, null!, _fakeRetryWorker, _currentPersonaMock.Object, logger, _timeProvider);
        Action act3 = () => new ConversationStateService(_conversationQueryMock.Object, _contactQueryMock.Object, null!, _currentPersonaMock.Object, logger, _timeProvider);
        Action act4 = () => new ConversationStateService(_conversationQueryMock.Object, _contactQueryMock.Object, _fakeRetryWorker, null!, logger, _timeProvider);
        Action act5 = () => new ConversationStateService(_conversationQueryMock.Object, _contactQueryMock.Object, _fakeRetryWorker, _currentPersonaMock.Object, null!, _timeProvider);
        Action act6 = () => new ConversationStateService(_conversationQueryMock.Object, _contactQueryMock.Object, _fakeRetryWorker, _currentPersonaMock.Object, logger, null!);

        act1.Should().Throw<ArgumentNullException>();
        act2.Should().Throw<ArgumentNullException>();
        act3.Should().Throw<ArgumentNullException>();
        act4.Should().Throw<ArgumentNullException>();
        act5.Should().Throw<ArgumentNullException>();
        act6.Should().Throw<ArgumentNullException>();
    }

    [Test]
    public async Task LoadConversationsForPersonaAsync_PopulatesConversationsFromQueryServices()
    {
        var channelId = ChannelId.New();
        var convRead = new ConversationSummaryReadModel(
            channelId,
            "Bob Chat",
            "Hey Alice",
            (ulong)_timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
            2,
            _peerId);

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
}
