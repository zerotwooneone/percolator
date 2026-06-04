using System;
using System.Collections.ObjectModel;
using System.Linq;
using Desktop.Wpf.Features.Self;
using Desktop.Wpf.Features.Sessions;
using Desktop.Wpf.Features.Sessions.Models;
using Desktop.Wpf.Features.Sessions.Queries;
using Desktop.Wpf.Features.Sessions.State;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NUnit.Framework;
using Percolator.Application.Identity;

namespace Desktop.Wpf.Tests;

[TestFixture]
public sealed class SessionsSidebarViewModelTests
{
    [Test]
    public void Items_rebuilds_when_state_connections_collection_changes()
    {
        // ARRANGE
        var state = new PeerConnectionStateService(Mock.Of<IServiceScopeFactory>(MockBehavior.Loose));
        var pendingWindowManager = new Mock<Desktop.Wpf.Shared.Windowing.IWindowManager>(MockBehavior.Loose);
        var ui = new TestUiDispatcher();
        var activeIdentity = new ActiveIdentityContext();
        var pendingMenu = new PendingHandshakesMenuViewModel(pendingWindowManager.Object, Mock.Of<MediatR.IMediator>(), state, activeIdentity, ui);
        var selection = new SelectedChannelModel();
        var identityStateService = new Mock<IIdentityStateService>();

        var sut = new SessionsSidebarViewModel(
            identityStateService.Object,
            pendingMenu,
            state,
            selection,
            ui,
            pendingWindowManager.Object);

        // ASSERT: Initially empty
        sut.Items.Cast<object>().Count().Should().Be(0);

        // ACT
        var a1 = new PeerConnectionStateSnapshot(
            Key: PeerConnectionKey.FromSessionId(Guid.NewGuid()),
            PeerId: Guid.NewGuid(),
            DisplayName: "Alice",
            Initials: "A",
            Status: PeerConnectionStatus.Direct,
            RelayHostPeerId: null,
            LastActivityUtc: System.DateTimeOffset.UtcNow);
        state.UpdateConnections(new[] { a1 });

        // ASSERT: One item added
        sut.Items.Cast<object>().Count().Should().Be(1);
        var item = sut.Items.Cast<PeerConnectionListItemViewModel>().Single();
        item.DisplayName.Value.Should().Be("Alice");
    }

    [Test]
    public void PeerConnectionKey_FromSessionId_GeneratesCanonicalFormat()
    {
        // ACT
        var sessionId = Guid.NewGuid();
        var key = PeerConnectionKey.FromSessionId(sessionId);
        var keyString = key.ToString();

        // ASSERT
        keyString.Should().Match($"SecureSession:{sessionId:N}");
    }

    [Test]
    public void PeerConnectionKey_FromPendingCorrelationId_GeneratesCanonicalFormat()
    {
        // ACT
        var correlationId = Guid.NewGuid();
        var key = PeerConnectionKey.FromPendingCorrelationId(correlationId);
        var keyString = key.ToString();

        // ASSERT
        keyString.Should().Match($"PendingCorrelation:{correlationId:N}");
    }

    [Test]
    public void PeerConnectionKey_FromPendingSessionId_GeneratesCanonicalFormat()
    {
        // ACT
        var pendingSessionId = Guid.NewGuid();
        var key = PeerConnectionKey.FromPendingSessionId(pendingSessionId);
        var keyString = key.ToString();

        // ASSERT
        keyString.Should().Match($"PendingSession:{pendingSessionId:N}");
    }
}
