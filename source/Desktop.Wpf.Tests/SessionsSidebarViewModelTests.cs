using System.Collections.ObjectModel;
using Desktop.Wpf.Features.Self;
using Desktop.Wpf.Features.Sessions;
using Desktop.Wpf.Features.Sessions.Models;
using Desktop.Wpf.Features.Sessions.State;
using Desktop.Wpf.Shared.Navigation;
using FluentAssertions;
using Moq;
using NUnit.Framework;

namespace Desktop.Wpf.Tests;

[TestFixture]
public sealed class SessionsSidebarViewModelTests
{
    [Test]
    public void Items_rebuilds_when_store_channels_collection_changes()
    {
        // ARRANGE
        var nav = new Mock<INavigationService>(MockBehavior.Loose);
        var sessionFactory = new Mock<ISessionScopeFactory>(MockBehavior.Loose);

        var channels = new ObservableCollection<SecureChannelModel>();
        var channelsRo = new ReadOnlyObservableCollection<SecureChannelModel>(channels);

        var pending = new ObservableCollection<PendingInvitationModel>();
        var pendingRo = new ReadOnlyObservableCollection<PendingInvitationModel>(pending);

        var store = new Mock<ISecureChannelsStore>(MockBehavior.Loose);
        store.SetupGet(s => s.Channels).Returns(channelsRo);
        store.SetupGet(s => s.PendingInbound).Returns(pendingRo);

        var pendingWindowManager = new Mock<Desktop.Wpf.Shared.Windowing.IWindowManager>(MockBehavior.Loose);
        var pendingMenu = new PendingHandshakesMenuViewModel(pendingWindowManager.Object, Mock.Of<MediatR.IMediator>(), store.Object);

        var sut = new SessionsSidebarViewModel(
            nav.Object,
            new SelfIdentityModel(),
            sessionFactory.Object,
            pendingMenu,
            store.Object);

        sut.Items.Count.Should().Be(0);

        // ACT
        channels.Add(new SecureChannelModel(
            key: SecureChannelKey.FromSessionId(System.Guid.NewGuid()),
            displayName: "Alice",
            initials: "A",
            kind: SecureChannelKind.Direct,
            lastUpdateUtc: System.DateTimeOffset.UtcNow));

        // ASSERT
        sut.Items.Count.Should().Be(1);
        sut.Items[0].DisplayName.Value.Should().Be("Alice");
    }
}
