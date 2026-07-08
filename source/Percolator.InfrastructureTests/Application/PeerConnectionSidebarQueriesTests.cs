using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;
using Percolator.Application.Sessions;
using Percolator.Cryptography;
using Percolator.Identity;
using Percolator.Identity.Model;
using Percolator.Infrastructure.Cryptography;
using Percolator.Infrastructure.Identity;
using Percolator.Infrastructure.Persistence;
using Percolator.Infrastructure.Sessions;

namespace Percolator.InfrastructureTests.Application;

[TestFixture]
public sealed class PeerConnectionSidebarQueriesTests : IDisposable
{
    private PercolatorDbContext? _db;
    private IClock? _clock;
    private PeerConnectionSidebarQueries? _queries;
    private DateTimeOffset _now;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<PercolatorDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        _db = new PercolatorDbContext(options);
        _now = new DateTimeOffset(2025, 1, 1, 12, 0, 0, TimeSpan.Zero);
        _clock = new TestClock(_now);
        _queries = new PeerConnectionSidebarQueries(_db, _clock);
    }

    [TearDown]
    public void TearDown()
    {
        _db?.Dispose();
    }

    public void Dispose()
    {
        _db?.Dispose();
    }

    [Test]
    public async Task LoadSidebarConnectionsAsync_ReturnsEstablishedSecureSessions()
    {
        // Arrange
        var selfIdentityId = new SelfId(1);
        var peerId = new Percolator.Identity.PeerId(12345);
        var sessionId = Guid.NewGuid();

        _db.PeerIdentities.Add(new PeerIdentityDbo
        {
            PeerId = peerId.Value,
            PublicIdentityId = Guid.NewGuid(),
            Name = "Test Peer",
            Version = 1,
            CreatedAtUtc = _now,
            UpdatedAtUtc = _now
        });

        _db.Sessions.Add(new SessionDbo
        {
            SelfIdentityId = selfIdentityId.Value,
            SessionId = sessionId,
            RemotePeerId = peerId.Value,
            ProtocolVersion = 1,
            RootKey = new byte[32],
            CreatedAtUtc = _now,
            LastUsedAtUtc = _now
        });

        await _db.SaveChangesAsync();

        // Act
        var result = await _queries.LoadSidebarConnectionsAsync((int)selfIdentityId.Value);

        // Assert
        var sessionDto = result.Should().HaveCount(1).And.Subject.Single();
        sessionDto.SelfIdentityId.Should().Be((int)selfIdentityId.Value);
        sessionDto.KeyType.Should().Be(SidebarPeerConnectionKeyType.SecureSession);
        sessionDto.KeyValue.Should().Be(sessionId);
        sessionDto.DisplayName.Should().Be("Test Peer");
        sessionDto.Initials.Should().Be("TP");
        sessionDto.Status.Should().Be(SidebarPeerConnectionStatus.Relay); // No DirectSession, so Relay
        sessionDto.RelayHostPeerId.Should().BeNull();
        sessionDto.LastActivityUtc.Should().Be(_now);
    }

    [Test]
    public async Task LoadSidebarConnectionsAsync_ComputesDirectStatusWhenDirectSessionExists()
    {
        // Arrange
        var selfIdentityId = new SelfId(1);
        var peerId = new Percolator.Identity.PeerId(23456);
        var sessionId = Guid.NewGuid();

        _db.PeerIdentities.Add(new PeerIdentityDbo
        {
            PeerId = peerId.Value,
            PublicIdentityId = Guid.NewGuid(),
            Name = "Test Peer",
            Version = 1,
            CreatedAtUtc = _now,
            UpdatedAtUtc = _now
        });

        _db.Sessions.Add(new SessionDbo
        {
            SelfIdentityId = selfIdentityId.Value,
            SessionId = sessionId,
            RemotePeerId = peerId.Value,
            ProtocolVersion = 1,
            RootKey = new byte[32],
            CreatedAtUtc = _now,
            LastUsedAtUtc = _now
        });

        _db.DirectSessions.Add(new DirectSessionDbo
        {
            SelfIdentityId = selfIdentityId.Value,
            RemotePeerId = peerId.Value,
            SessionId = sessionId
        });

        await _db.SaveChangesAsync();

        // Act
        var result = await _queries.LoadSidebarConnectionsAsync((int)selfIdentityId.Value);

        // Assert
        var sessionDto = result.Should().HaveCount(1).And.Subject.Single();
        sessionDto.Status.Should().Be(SidebarPeerConnectionStatus.Direct);
    }

    [Test]
    public async Task LoadSidebarConnectionsAsync_ReturnsPendingOutboundFromSentInvitations()
    {
        // Arrange
        var selfIdentityId = new SelfId(1);
        var correlationId = Guid.NewGuid();
        var targetPeerId = 34567;

        _db.SentInvitations.Add(new SentInvitationDbo
        {
            SelfIdentityId = selfIdentityId.Value,
            RequestCorrelationId = correlationId.ToString(),
            SignedPreKeyId = Guid.NewGuid(),
            TargetPeerId = (uint)targetPeerId,
            TargetDisplayName = "Target Peer",
            InviteRouteKind = (int)InviteRouteKind.Direct,
            CreatedAtUtc = _now,
            ExpiresAtUtc = _now.AddHours(1)
        });

        await _db.SaveChangesAsync();

        // Act
        var result = await _queries.LoadSidebarConnectionsAsync((int)selfIdentityId.Value);

        // Assert
        var pendingDto = result.Should().HaveCount(1).And.Subject.Single();
        pendingDto.SelfIdentityId.Should().Be((int)selfIdentityId.Value);
        pendingDto.KeyType.Should().Be(SidebarPeerConnectionKeyType.PendingCorrelation);
        pendingDto.KeyValue.Should().Be(correlationId);
        pendingDto.DisplayName.Should().Be("Target Peer");
        pendingDto.Initials.Should().Be("TP");
        pendingDto.Status.Should().Be(SidebarPeerConnectionStatus.PendingOutbound);
        pendingDto.RelayHostPeerId.Should().BeNull();
        pendingDto.LastActivityUtc.Should().Be(_now);
    }

    [Test]
    public async Task LoadSidebarConnectionsAsync_ExcludesExpiredInvitations()
    {
        // Arrange
        var selfIdentityId = new SelfId(1);
        var correlationId = Guid.NewGuid();

        _db.SentInvitations.Add(new SentInvitationDbo
        {
            SelfIdentityId = selfIdentityId.Value,
            RequestCorrelationId = correlationId.ToString(),
            SignedPreKeyId = Guid.NewGuid(),
            CreatedAtUtc = _now.AddHours(-2),
            ExpiresAtUtc = _now.AddHours(-1) // Expired
        });

        await _db.SaveChangesAsync();

        // Act
        var result = await _queries.LoadSidebarConnectionsAsync((int)selfIdentityId.Value);

        // Assert
        result.Should().BeEmpty();
    }

    [Test]
    public async Task LoadSidebarConnectionsAsync_SuppressesPendingOutboundWhenSessionExistsForTargetPeerId()
    {
        // Arrange
        var selfIdentityId = new SelfId(1);
        var peerId = new Percolator.Identity.PeerId(45678);
        var targetPeerId = 56789;
        var sessionId = Guid.NewGuid();
        var correlationId = Guid.NewGuid();

        _db.PeerIdentities.Add(new PeerIdentityDbo
        {
            PeerId = peerId.Value,
            PublicIdentityId = Guid.NewGuid(),
            Name = "Test Peer",
            Version = 1,
            CreatedAtUtc = _now,
            UpdatedAtUtc = _now
        });

        _db.Sessions.Add(new SessionDbo
        {
            SelfIdentityId = selfIdentityId.Value,
            SessionId = sessionId,
            RemotePeerId = peerId.Value,
            ProtocolVersion = 1,
            RootKey = new byte[32],
            CreatedAtUtc = _now,
            LastUsedAtUtc = _now
        });

        _db.SentInvitations.Add(new SentInvitationDbo
        {
            SelfIdentityId = selfIdentityId.Value,
            RequestCorrelationId = correlationId.ToString(),
            SignedPreKeyId = Guid.NewGuid(),
            TargetPeerId = (uint)targetPeerId, // Same peer as established session
            TargetDisplayName = "Test Peer",
            InviteRouteKind = (int)InviteRouteKind.Direct,
            CreatedAtUtc = _now,
            ExpiresAtUtc = _now.AddHours(1)
        });

        await _db.SaveChangesAsync();

        // Act
        var result = await _queries.LoadSidebarConnectionsAsync((int)selfIdentityId.Value);

        // Assert
        result.Should().HaveCount(1); // Only the established session
        result[0].KeyType.Should().Be(SidebarPeerConnectionKeyType.SecureSession);
    }

    [Test]
    public async Task LoadSidebarConnectionsAsync_DoesNotIncludeInboundPendingFromPendingSessions()
    {
        // Arrange
        var selfIdentityId = new SelfId(1);
        var peerId = new Percolator.Identity.PeerId(67890);

        _db.PendingSessions.Add(new PendingSessionDbo
        {
            SelfIdentityId = selfIdentityId.Value,
            Id = Guid.NewGuid(),
            RemotePeerId = peerId.Value,
            State = (int)ApprovalState.AwaitingApproval,
            CreatedAtUtc = _now
        });

        await _db.SaveChangesAsync();

        // Act
        var result = await _queries.LoadSidebarConnectionsAsync((int)selfIdentityId.Value);

        // Assert
        result.Should().BeEmpty(); // Inbound pending should not appear in sidebar
    }

    [Test]
    public async Task LoadSidebarConnectionsAsync_ReturnsBothEstablishedAndPendingOutbound()
    {
        // Arrange
        var selfIdentityId = new SelfId(1);
        var peerId1 = new Percolator.Identity.PeerId(78901);
        var peerId2 = new Percolator.Identity.PeerId(89012);
        var sessionId = Guid.NewGuid();
        var correlationId = Guid.NewGuid();
        var targetPeerId = 90123;

        _db.PeerIdentities.Add(new PeerIdentityDbo
        {
            PeerId = peerId1.Value,
            PublicIdentityId = Guid.NewGuid(),
            Name = "Peer 1",
            Version = 1,
            CreatedAtUtc = _now,
            UpdatedAtUtc = _now
        });

        _db.PeerIdentities.Add(new PeerIdentityDbo
        {
            PeerId = peerId2.Value,
            PublicIdentityId = Guid.NewGuid(),
            Name = "Peer 2",
            Version = 1,
            CreatedAtUtc = _now,
            UpdatedAtUtc = _now
        });

        _db.Sessions.Add(new SessionDbo
        {
            SelfIdentityId = selfIdentityId.Value,
            SessionId = sessionId,
            RemotePeerId = peerId1.Value,
            ProtocolVersion = 1,
            RootKey = new byte[32],
            CreatedAtUtc = _now,
            LastUsedAtUtc = _now
        });

        _db.SentInvitations.Add(new SentInvitationDbo
        {
            SelfIdentityId = selfIdentityId.Value,
            RequestCorrelationId = correlationId.ToString(),
            SignedPreKeyId = Guid.NewGuid(),
            TargetPeerId = (uint)targetPeerId,
            TargetDisplayName = "Peer 2",
            InviteRouteKind = (int)InviteRouteKind.Direct,
            CreatedAtUtc = _now,
            ExpiresAtUtc = _now.AddHours(1)
        });

        await _db.SaveChangesAsync();

        // Act
        var result = await _queries.LoadSidebarConnectionsAsync((int)selfIdentityId.Value);

        // Assert
        result.Should().HaveCount(2);
        result.Should().Contain(r => r.KeyType == SidebarPeerConnectionKeyType.SecureSession);
        result.Should().Contain(r => r.KeyType == SidebarPeerConnectionKeyType.PendingCorrelation);
    }

    private sealed class TestClock : IClock
    {
        public TestClock(DateTimeOffset now)
        {
            UtcNow = now;
        }

        public DateTimeOffset UtcNow { get; }
    }
}
