using System;
using System.Threading;
using Desktop.Wpf.Features.Chat;
using Desktop.Wpf.Features.Sessions;
using Desktop.Wpf.Features.Sessions.Models;
using Desktop.Wpf.Features.Sessions.Queries;
using Desktop.Wpf.Features.Sessions.State;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NUnit.Framework;

namespace Desktop.Wpf.Tests;

[TestFixture]
public sealed class SelectedChannelPaneViewModelTests
{
    private SynchronizationContext? _originalContext;

    [SetUp]
    public void SetupSyncContext()
    {
        _originalContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new TestSynchronizationContext());
    }

    [TearDown]
    public void RestoreSyncContext()
    {
        SynchronizationContext.SetSynchronizationContext(_originalContext);
    }

    [Test]
    public void SelectingPendingCorrelationKey_ShowsPendingState()
    {
        // ARRANGE
        var scopeFactory = new Mock<IServiceScopeFactory>(MockBehavior.Loose);
        var state = new PeerConnectionStateService(scopeFactory.Object);
        var selection = new SelectedChannelModel();
        var sessionFactory = new Mock<ISessionScopeFactory>(MockBehavior.Loose);
        var reloadCoordinator = new Mock<IChatReloadCoordinator>(MockBehavior.Loose);

        var sut = new SelectedChannelPaneViewModel(selection, state, sessionFactory.Object, reloadCoordinator.Object);

        // Add a pending outbound connection
        var correlationId = Guid.NewGuid();
        var pendingModel = new PeerConnectionModel(
            PeerConnectionKey.FromPendingCorrelationId(correlationId),
            peerId: null,
            displayName: "Pending Peer",
            initials: "PP",
            status: PeerConnectionStatus.PendingOutbound,
            lastActivityUtc: DateTimeOffset.UtcNow);
        state.UpdateConnections(new[] { new PeerConnectionStateSnapshot(
            PeerConnectionKey.FromPendingCorrelationId(correlationId),
            PeerId: null,
            DisplayName: "Pending Peer",
            Initials: "PP",
            Status: PeerConnectionStatus.PendingOutbound,
            RelayHostPeerId: null,
            LastActivityUtc: DateTimeOffset.UtcNow) });

        // ACT
        selection.SelectedKey.Value = PeerConnectionKey.FromPendingCorrelationId(correlationId);

        // ASSERT
        // TestSynchronizationContext processes observables synchronously
        sut.BannerText.Value.Should().Be("Establishing…");
        sut.IsInputEnabled.Value.Should().BeFalse();
    }

    [Test]
    public void SelectingSecureSessionKey_ShowsActiveState()
    {
        // ARRANGE
        var scopeFactory = new Mock<IServiceScopeFactory>(MockBehavior.Loose);
        var state = new PeerConnectionStateService(scopeFactory.Object);
        var selection = new SelectedChannelModel();
        var sessionFactory = new Mock<ISessionScopeFactory>(MockBehavior.Loose);
        var reloadCoordinator = new Mock<IChatReloadCoordinator>(MockBehavior.Loose);

        var sut = new SelectedChannelPaneViewModel(selection, state, sessionFactory.Object, reloadCoordinator.Object);

        // Add an established session
        var sessionId = Guid.NewGuid();
        var peerId = Guid.NewGuid();
        state.UpdateConnections(new[] { new PeerConnectionStateSnapshot(
            PeerConnectionKey.FromSessionId(sessionId),
            PeerId: peerId,
            DisplayName: "Alice",
            Initials: "A",
            Status: PeerConnectionStatus.Direct,
            RelayHostPeerId: null,
            LastActivityUtc: DateTimeOffset.UtcNow) });

        // ACT
        selection.SelectedKey.Value = PeerConnectionKey.FromSessionId(sessionId);

        // ASSERT
        // TestSynchronizationContext processes observables synchronously
        sut.BannerText.Value.Should().Be("E2E Encryption Established");
        sut.IsInputEnabled.Value.Should().BeTrue();
    }
}
