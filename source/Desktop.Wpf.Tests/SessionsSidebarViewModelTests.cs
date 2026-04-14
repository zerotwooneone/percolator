using System;
using System.Collections.ObjectModel;
using Desktop.Wpf.Features.Self;
using Desktop.Wpf.Features.Sessions;
using Desktop.Wpf.Features.Sessions.Queries;
using Desktop.Wpf.Features.Sessions.State;
using Desktop.Wpf.Shared.Navigation;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NUnit.Framework;

namespace Desktop.Wpf.Tests;

[TestFixture]
public sealed class SessionsSidebarViewModelTests
{
    [Test]
    public void Items_rebuilds_when_state_connections_collection_changes()
    {
        // ARRANGE
        var nav = new Mock<INavigationService>(MockBehavior.Loose);

        var state = new PeerConnectionStateService(Mock.Of<IServiceScopeFactory>(MockBehavior.Loose));
        var pendingWindowManager = new Mock<Desktop.Wpf.Shared.Windowing.IWindowManager>(MockBehavior.Loose);
        var ui = new TestUiDispatcher();
        var pendingMenu = new PendingHandshakesMenuViewModel(pendingWindowManager.Object, Mock.Of<MediatR.IMediator>(), state, ui);
        var selection = new SelectedChannelModel();

        var sut = new SessionsSidebarViewModel(
            nav.Object,
            new SelfIdentityModel(),
            pendingMenu,
            state,
            selection,
            ui);

        ((System.Collections.Generic.ICollection<PeerConnectionListItemViewModel>)sut.Items).Count.Should().Be(0);

        // ACT
        var a1 = new PeerConnectionStateSnapshot(
            ConnectionId: Guid.NewGuid(),
            PeerId: Guid.NewGuid(),
            DisplayName: "Alice",
            Initials: "A",
            Status: PeerConnectionStatus.Direct,
            RelayHostPeerId: null,
            LastActivityUtc: System.DateTimeOffset.UtcNow);
        state.UpdateConnections(new[] { a1 });

        // ASSERT
        ((System.Collections.Generic.ICollection<PeerConnectionListItemViewModel>)sut.Items).Count.Should().Be(1);
        ((System.Collections.Generic.IList<PeerConnectionListItemViewModel>)sut.Items)[0].DisplayName.Value.Should().Be("Alice");
    }
}
