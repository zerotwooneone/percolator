using System;
using System.Linq;
using Desktop.Wpf.Features.Sessions;
using Desktop.Wpf.Features.Sessions.Queries;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NUnit.Framework;

namespace Desktop.Wpf.Tests;

[TestFixture]
public sealed class PeerConnectionStateServiceTests
{
    [Test]
    public void UpdateConnections_adds_updates_and_removes_models_based_on_snapshot()
    {
        // ARRANGE
        var scopeFactory = new Mock<IServiceScopeFactory>(MockBehavior.Loose);
        var sut = new PeerConnectionStateService(scopeFactory.Object);

        var a1 = new PeerConnectionStateSnapshot(
            ConnectionId: Guid.NewGuid(),
            PeerId: Guid.NewGuid(),
            DisplayName: "Alice",
            Initials: "A",
            Status: PeerConnectionStatus.Direct,
            RelayHostPeerId: null,
            LastActivityUtc: DateTimeOffset.UtcNow);

        var b1 = new PeerConnectionStateSnapshot(
            ConnectionId: Guid.NewGuid(),
            PeerId: Guid.NewGuid(),
            DisplayName: "Bob",
            Initials: "B",
            Status: PeerConnectionStatus.Relay,
            RelayHostPeerId: null,
            LastActivityUtc: DateTimeOffset.UtcNow.AddMinutes(-1));

        // ACT 1: initial load
        sut.UpdateConnections(new[] { a1, b1 });

        // ASSERT 1
        sut.Connections.Should().HaveCount(2);
        sut.Connections.Select(x => x.ConnectionId).Should().BeEquivalentTo(new[] { a1.ConnectionId, b1.ConnectionId });

        // ACT 2: update alice, remove bob
        var a2 = a1 with { DisplayName = "Alice Updated", Status = PeerConnectionStatus.Group };
        sut.UpdateConnections(new[] { a2 });

        // ASSERT 2
        sut.Connections.Should().HaveCount(1);
        sut.Connections[0].ConnectionId.Should().Be(a1.ConnectionId);
        sut.Connections[0].DisplayName.CurrentValue.Should().Be("Alice Updated");
        sut.Connections[0].Status.CurrentValue.Should().Be(PeerConnectionStatus.Group);
    }

    [Test]
    public void UpdatePendingInbound_adds_updates_and_removes_models_based_on_snapshot()
    {
        // ARRANGE
        var scopeFactory = new Mock<IServiceScopeFactory>(MockBehavior.Loose);
        var sut = new PeerConnectionStateService(scopeFactory.Object);

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
            RelayEndpoint: null);

        // ACT 1
        sut.UpdatePendingInbound(new[] { p1 });

        // ASSERT 1
        sut.PendingInbound.Should().HaveCount(1);
        sut.PendingInbound[0].PendingSessionId.Should().Be(p1.PendingSessionId);
        sut.PendingInbound[0].PeerName.CurrentValue.Should().Be("Alice");
        sut.PendingInbound[0].IsRelayed.CurrentValue.Should().BeFalse();

        // ACT 2: update + then remove
        var p2 = p1 with { PeerName = "Alice Updated", IsRelayed = true };
        sut.UpdatePendingInbound(new[] { p2 });

        sut.PendingInbound.Should().HaveCount(1);
        sut.PendingInbound[0].PeerName.CurrentValue.Should().Be("Alice Updated");
        sut.PendingInbound[0].IsRelayed.CurrentValue.Should().BeTrue();

        sut.UpdatePendingInbound(Array.Empty<PendingInboundSnapshot>());
        sut.PendingInbound.Should().BeEmpty();
    }
}
