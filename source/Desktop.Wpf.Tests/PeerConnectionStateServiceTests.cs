using System;
using System.Linq;
using System.Threading;
using Desktop.Wpf.Features.Sessions;
using Desktop.Wpf.Features.Sessions.Models;
using Desktop.Wpf.Features.Sessions.Queries;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NUnit.Framework;
using Percolator.Application.Sessions;
using Percolator.Identity;

namespace Desktop.Wpf.Tests;

[TestFixture]
public sealed class PeerConnectionStateServiceTests
{
    [Test]
    public void UpdateConnections_GivenInitialSnapshot_PopulatesConnectionsCollection()
    {
        // ARRANGE
        var scopeFactory = new Mock<IServiceScopeFactory>(MockBehavior.Loose);
        var sut = new PeerConnectionStateService(scopeFactory.Object);

        var a1 = new PeerConnectionStateSnapshot(
            Key: PeerConnectionKey.FromSessionId(Guid.NewGuid()),
            PeerId: Guid.NewGuid(),
            DisplayName: "Alice",
            Initials: "A",
            Status: PeerConnectionStatus.Direct,
            RelayHostPeerId: null,
            LastActivityUtc: DateTimeOffset.UtcNow);

        var b1 = new PeerConnectionStateSnapshot(
            Key: PeerConnectionKey.FromSessionId(Guid.NewGuid()),
            PeerId: Guid.NewGuid(),
            DisplayName: "Bob",
            Initials: "B",
            Status: PeerConnectionStatus.Relay,
            RelayHostPeerId: null,
            LastActivityUtc: DateTimeOffset.UtcNow.AddMinutes(-1));

        // ACT
        sut.UpdateConnections(new[] { a1, b1 });

        // ASSERT
        sut.Connections.Should().HaveCount(2);
        sut.Connections.Select(x => x.Key).Should().BeEquivalentTo(new[] { a1.Key, b1.Key });
    }

    [Test]
    public void UpdateConnections_GivenUpdatedSnapshot_UpdatesExistingConnection()
    {
        // ARRANGE
        var scopeFactory = new Mock<IServiceScopeFactory>(MockBehavior.Loose);
        var sut = new PeerConnectionStateService(scopeFactory.Object);

        var a1 = new PeerConnectionStateSnapshot(
            Key: PeerConnectionKey.FromSessionId(Guid.NewGuid()),
            PeerId: Guid.NewGuid(),
            DisplayName: "Alice",
            Initials: "A",
            Status: PeerConnectionStatus.Direct,
            RelayHostPeerId: null,
            LastActivityUtc: DateTimeOffset.UtcNow);

        sut.UpdateConnections(new[] { a1 });

        // ACT
        var a2 = a1 with { DisplayName = "Alice Updated", Status = PeerConnectionStatus.Group };
        sut.UpdateConnections(new[] { a2 });

        // ASSERT
        sut.Connections.Should().HaveCount(1);
        var connection = sut.Connections.Single(x => x.Key == a1.Key);
        connection.DisplayName.CurrentValue.Should().Be("Alice Updated");
        connection.Status.CurrentValue.Should().Be(PeerConnectionStatus.Group);
    }

    [Test]
    public void UpdateConnections_GivenSnapshotWithoutExistingKey_RemovesConnection()
    {
        // ARRANGE
        var scopeFactory = new Mock<IServiceScopeFactory>(MockBehavior.Loose);
        var sut = new PeerConnectionStateService(scopeFactory.Object);

        var a1 = new PeerConnectionStateSnapshot(
            Key: PeerConnectionKey.FromSessionId(Guid.NewGuid()),
            PeerId: Guid.NewGuid(),
            DisplayName: "Alice",
            Initials: "A",
            Status: PeerConnectionStatus.Direct,
            RelayHostPeerId: null,
            LastActivityUtc: DateTimeOffset.UtcNow);

        var b1 = new PeerConnectionStateSnapshot(
            Key: PeerConnectionKey.FromSessionId(Guid.NewGuid()),
            PeerId: Guid.NewGuid(),
            DisplayName: "Bob",
            Initials: "B",
            Status: PeerConnectionStatus.Relay,
            RelayHostPeerId: null,
            LastActivityUtc: DateTimeOffset.UtcNow.AddMinutes(-1));

        sut.UpdateConnections(new[] { a1, b1 });

        // ACT
        sut.UpdateConnections(new[] { a1 });

        // ASSERT
        sut.Connections.Should().HaveCount(1);
        sut.Connections.Single().Key.Should().Be(a1.Key);
    }

    [Test]
    public void UpdatePendingInbound_GivenInitialSnapshot_PopulatesPendingInboundCollection()
    {
        // ARRANGE
        var scope = new Mock<IServiceScope>();
        var provider = new Mock<IServiceProvider>();
        var sidebarQueries = new Mock<Percolator.Application.Sessions.IPeerConnectionSidebarQueries>();
        sidebarQueries.Setup(q => q.LoadSidebarConnectionsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<Percolator.Application.Sessions.SidebarPeerConnectionDto>());
        provider.Setup(p => p.GetService(typeof(Percolator.Application.Sessions.IPeerConnectionSidebarQueries)))
            .Returns(sidebarQueries.Object);
        
        var inboundQueries = new Mock<Percolator.Application.Sessions.IPeerConnectionQueries>();
        inboundQueries.Setup(q => q.LoadPendingInboundAsync(It.IsAny<SelfId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<Percolator.Application.Sessions.PendingInboundSnapshot>());
        provider.Setup(p => p.GetService(typeof(Percolator.Application.Sessions.IPeerConnectionQueries)))
            .Returns(inboundQueries.Object);
        
        scope.Setup(s => s.ServiceProvider).Returns(provider.Object);
        
        var scopeFactory = new Mock<IServiceScopeFactory>();
        scopeFactory.Setup(f => f.CreateScope()).Returns(scope.Object);
        
        var sut = new PeerConnectionStateService(scopeFactory.Object);
        var selfId = new SelfId(1);
        
        sut.InitializeAsync(selfId, CancellationToken.None).GetAwaiter().GetResult();

        var p1 = new PendingInboundSnapshot(
            PendingSessionId: Guid.NewGuid(),
            RequestCorrelationId: Guid.NewGuid(),
            PeerId: Guid.NewGuid(),
            PeerName: "Alice",
            InviterFingerprintHex: null,
            CreatedAtUtc: DateTimeOffset.UtcNow,
            ExpiresAtUtc: null,
            IsRelayed: false,
            RelayPeerId: null,
            RelayPeerName: null,
            RelayEndpoint: null,
            SelfIdentityId: selfId);

        // ACT
        sut.UpdatePendingInbound(selfId, new[] { p1 });

        // ASSERT
        sut.PendingInbound.Should().HaveCount(1);
        var invitation = sut.PendingInbound.Single(x => x.PendingSessionId == p1.PendingSessionId);
        invitation.PeerName.CurrentValue.Should().Be("Alice");
        invitation.IsRelayed.CurrentValue.Should().BeFalse();
    }

    [Test]
    public void UpdatePendingInbound_GivenUpdatedSnapshot_UpdatesExistingInvitation()
    {
        // ARRANGE
        var scope = new Mock<IServiceScope>();
        var provider = new Mock<IServiceProvider>();
        var sidebarQueries = new Mock<Percolator.Application.Sessions.IPeerConnectionSidebarQueries>();
        sidebarQueries.Setup(q => q.LoadSidebarConnectionsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<Percolator.Application.Sessions.SidebarPeerConnectionDto>());
        provider.Setup(p => p.GetService(typeof(Percolator.Application.Sessions.IPeerConnectionSidebarQueries)))
            .Returns(sidebarQueries.Object);
        
        var inboundQueries = new Mock<Percolator.Application.Sessions.IPeerConnectionQueries>();
        inboundQueries.Setup(q => q.LoadPendingInboundAsync(It.IsAny<SelfId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<Percolator.Application.Sessions.PendingInboundSnapshot>());
        provider.Setup(p => p.GetService(typeof(Percolator.Application.Sessions.IPeerConnectionQueries)))
            .Returns(inboundQueries.Object);
        
        scope.Setup(s => s.ServiceProvider).Returns(provider.Object);
        
        var scopeFactory = new Mock<IServiceScopeFactory>();
        scopeFactory.Setup(f => f.CreateScope()).Returns(scope.Object);
        
        var sut = new PeerConnectionStateService(scopeFactory.Object);
        var selfId = new SelfId(1);
        
        sut.InitializeAsync(selfId, CancellationToken.None).GetAwaiter().GetResult();

        var p1 = new PendingInboundSnapshot(
            PendingSessionId: Guid.NewGuid(),
            RequestCorrelationId: Guid.NewGuid(),
            PeerId: Guid.NewGuid(),
            PeerName: "Alice",
            InviterFingerprintHex: null,
            CreatedAtUtc: DateTimeOffset.UtcNow,
            ExpiresAtUtc: null,
            IsRelayed: false,
            RelayPeerId: null,
            RelayPeerName: null,
            RelayEndpoint: null,
            SelfIdentityId: selfId);

        sut.UpdatePendingInbound(selfId, new[] { p1 });

        // ACT
        var p2 = p1 with { PeerName = "Alice Updated", IsRelayed = true };
        sut.UpdatePendingInbound(selfId, new[] { p2 });

        // ASSERT
        sut.PendingInbound.Should().HaveCount(1);
        var invitation = sut.PendingInbound.Single(x => x.PendingSessionId == p1.PendingSessionId);
        invitation.PeerName.CurrentValue.Should().Be("Alice Updated");
        invitation.IsRelayed.CurrentValue.Should().BeTrue();
    }

    [Test]
    public void UpdatePendingInbound_GivenEmptySnapshot_RemovesAllInvitations()
    {
        // ARRANGE
        var scope = new Mock<IServiceScope>();
        var provider = new Mock<IServiceProvider>();
        var sidebarQueries = new Mock<Percolator.Application.Sessions.IPeerConnectionSidebarQueries>();
        sidebarQueries.Setup(q => q.LoadSidebarConnectionsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<Percolator.Application.Sessions.SidebarPeerConnectionDto>());
        provider.Setup(p => p.GetService(typeof(Percolator.Application.Sessions.IPeerConnectionSidebarQueries)))
            .Returns(sidebarQueries.Object);
        
        var inboundQueries = new Mock<Percolator.Application.Sessions.IPeerConnectionQueries>();
        inboundQueries.Setup(q => q.LoadPendingInboundAsync(It.IsAny<SelfId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<Percolator.Application.Sessions.PendingInboundSnapshot>());
        provider.Setup(p => p.GetService(typeof(Percolator.Application.Sessions.IPeerConnectionQueries)))
            .Returns(inboundQueries.Object);
        
        scope.Setup(s => s.ServiceProvider).Returns(provider.Object);
        
        var scopeFactory = new Mock<IServiceScopeFactory>();
        scopeFactory.Setup(f => f.CreateScope()).Returns(scope.Object);
        
        var sut = new PeerConnectionStateService(scopeFactory.Object);
        var selfId = new SelfId(1);
        
        sut.InitializeAsync(selfId, CancellationToken.None).GetAwaiter().GetResult();

        var p1 = new PendingInboundSnapshot(
            PendingSessionId: Guid.NewGuid(),
            RequestCorrelationId: Guid.NewGuid(),
            PeerId: Guid.NewGuid(),
            PeerName: "Alice",
            InviterFingerprintHex: null,
            CreatedAtUtc: DateTimeOffset.UtcNow,
            ExpiresAtUtc: null,
            IsRelayed: false,
            RelayPeerId: null,
            RelayPeerName: null,
            RelayEndpoint: null,
            SelfIdentityId: selfId);

        sut.UpdatePendingInbound(selfId, new[] { p1 });

        // ACT
        sut.UpdatePendingInbound(selfId, Array.Empty<PendingInboundSnapshot>());

        // ASSERT
        sut.PendingInbound.Should().BeEmpty();
    }
}
