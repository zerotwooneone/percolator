// SPECIFICATION GOVERNANCE: Read Percolator.Desktop/spec/ViewModel.spec.md before modifying this file.

namespace Percolator.Desktop.Tests.Features.Chats;

using System;
using System.Linq;
using System.Threading;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using ObservableCollections;
using Percolator.Desktop.Common.Navigation;
using Percolator.Desktop.Common.Services;
using Percolator.Desktop.Features.Chats.Models;
using Percolator.Desktop.Features.Chats.ViewModels;
using Percolator.Desktop.Features.Shell.Models;
using Percolator.Desktop.Features.Transports.Models;
using Percolator.Desktop.Features.Transports.Services;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities;
using R3;

[TestFixture]
public sealed class ChatsSidebarViewModelTests
{
    private ShellNavigationStateService _navigationService = null!;
    private FakeConversationStateService _fakeConversationService = null!;
    private Mock<IRelayStateService> _relayStateServiceMock = null!;
    private ObservableList<RelayChannelModel> _relaysList = null!;
    private SynchronizationContext? _previousSyncContext;

    private PublicIdentityId _relayId;

    private sealed class ImmediateSynchronizationContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) => d(state);
        public override void Send(SendOrPostCallback d, object? state) => d(state);
    }

    [SetUp]
    public void SetUp()
    {
        _previousSyncContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new ImmediateSynchronizationContext());

        _navigationService = new ShellNavigationStateService(NullLogger<ShellNavigationStateService>.Instance);
        _fakeConversationService = new FakeConversationStateService();
        _relayStateServiceMock = new Mock<IRelayStateService>();

        _relayId = PublicIdentityId.New();

        _relaysList = new ObservableList<RelayChannelModel>();
        _relaysList.Add(new RelayChannelModel(_relayId, "Primary Post Office", RelayEndpoint.Parse("percolator://relay.org")));
        var relaysView = _relaysList.CreateView(r => r);
        _relayStateServiceMock.SetupGet(r => r.Relays).Returns(relaysView);
    }

    [TearDown]
    public void TearDown()
    {
        SynchronizationContext.SetSynchronizationContext(_previousSyncContext);
        _navigationService.Dispose();
        _fakeConversationService.Dispose();
    }

    private ChatsSidebarViewModel CreateViewModel()
    {
        return new ChatsSidebarViewModel(
            _navigationService,
            _fakeConversationService,
            _relayStateServiceMock.Object,
            NullLogger<ChatsSidebarViewModel>.Instance);
    }

    [Test]
    public void Constructor_WithNullDependencies_ThrowsArgumentNullException()
    {
        var logger = NullLogger<ChatsSidebarViewModel>.Instance;

        Action act1 = () => new ChatsSidebarViewModel(null!, _fakeConversationService, _relayStateServiceMock.Object, logger);
        Action act2 = () => new ChatsSidebarViewModel(_navigationService, null!, _relayStateServiceMock.Object, logger);
        Action act3 = () => new ChatsSidebarViewModel(_navigationService, _fakeConversationService, null!, logger);
        Action act4 = () => new ChatsSidebarViewModel(_navigationService, _fakeConversationService, _relayStateServiceMock.Object, null!);

        act1.Should().Throw<ArgumentNullException>();
        act2.Should().Throw<ArgumentNullException>();
        act3.Should().Throw<ArgumentNullException>();
        act4.Should().Throw<ArgumentNullException>();
    }

    [Test]
    public void WhenActiveLeftRailTargetIsDirectMessages_ShowsP2PChatsAndPendingItems()
    {
        using var vm = CreateViewModel();
        var now = DateTimeOffset.UtcNow;

        var directPeerId = PublicIdentityId.New();
        var relayedPeerId = PublicIdentityId.New();
        var pendingPeerId = PublicIdentityId.New();

        var directConv = new ConversationSummaryModel(ChannelId.New(), directPeerId, "Bob P2P", ConversationRouteType.DirectP2P, null, "", now);
        var relayedConv = new ConversationSummaryModel(ChannelId.New(), relayedPeerId, "Charlie Relay", ConversationRouteType.Relayed, _relayId, "", now);
        var pendingConv = new ConversationSummaryModel(ChannelId.New(), pendingPeerId, "Dave Offline", ConversationRouteType.DirectP2P, null, "", now, isPendingOutbound: true);

        _fakeConversationService.BackingList.Add(directConv);
        _fakeConversationService.BackingList.Add(relayedConv);
        _fakeConversationService.BackingList.Add(pendingConv);

        _navigationService.NavigateToDirectMessages();

        vm.HeaderTitle.Should().Be("Direct Messages");
        vm.Conversations.Select(c => c.DisplayName)
            .Should().Contain(new[] { "Bob P2P", "Dave Offline" })
            .And.NotContain("Charlie Relay");
        vm.HasConversations.Should().BeTrue();
    }

    [Test]
    public void WhenActiveLeftRailTargetIsRelay_ShowsRelayedChatsForThatRelay()
    {
        using var vm = CreateViewModel();
        var now = DateTimeOffset.UtcNow;

        var directPeerId = PublicIdentityId.New();
        var relayedPeerId = PublicIdentityId.New();
        var otherRelayPeerId = PublicIdentityId.New();

        var directConv = new ConversationSummaryModel(ChannelId.New(), directPeerId, "Bob P2P", ConversationRouteType.DirectP2P, null, "", now);
        var relayedConv = new ConversationSummaryModel(ChannelId.New(), relayedPeerId, "Charlie Relay", ConversationRouteType.Relayed, _relayId, "", now);
        var otherRelayConv = new ConversationSummaryModel(ChannelId.New(), otherRelayPeerId, "Eve Other Relay", ConversationRouteType.Relayed, PublicIdentityId.New(), "", now);

        _fakeConversationService.BackingList.Add(directConv);
        _fakeConversationService.BackingList.Add(relayedConv);
        _fakeConversationService.BackingList.Add(otherRelayConv);

        _navigationService.NavigateToRelay(_relayId);

        vm.HeaderTitle.Should().Be("Primary Post Office");
        vm.Conversations.Should().ContainSingle();
        vm.Conversations.First().DisplayName.Should().Be("Charlie Relay");
        vm.HasConversations.Should().BeTrue();
    }

    [Test]
    public void SelectingConversation_NotifiesShellNavigationService()
    {
        using var vm = CreateViewModel();
        var channelId = ChannelId.New();
        var directConv = new ConversationSummaryModel(channelId, PublicIdentityId.New(), "Bob P2P", ConversationRouteType.DirectP2P, null, "", DateTimeOffset.UtcNow);
        _fakeConversationService.BackingList.Add(directConv);

        _navigationService.NavigateToDirectMessages();

        vm.SelectedConversation = directConv;

        _navigationService.SelectedConversationId.CurrentValue.Should().Be(channelId);
    }

    [Test]
    public void SelectedConversationIdFromNavigation_UpdatesSelectedConversation()
    {
        using var vm = CreateViewModel();
        var channelId = ChannelId.New();
        var directConv = new ConversationSummaryModel(channelId, PublicIdentityId.New(), "Bob P2P", ConversationRouteType.DirectP2P, null, "", DateTimeOffset.UtcNow);
        _fakeConversationService.BackingList.Add(directConv);

        _navigationService.NavigateToDirectMessages();

        _navigationService.SelectConversation(channelId);

        vm.SelectedConversation.Should().NotBeNull();
        vm.SelectedConversation!.ConversationId.Should().Be(channelId);
    }
}
