using FluentAssertions;
using Moq;
using Percolator.Cryptography;
using Percolator.Cryptography.Primitives;

namespace Percolator.CryptographyTests;

file sealed class TestClock2 : IClock
{
    public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.UtcNow;
}

file sealed class NoopCrypto : ICryptoPrimitives
{
    public HandshakeResponseMessage CreateHandshakeResponse(HandshakeInvitation invitation, IKeyStore keyStore)
        => HandshakeResponseMessage.FromBytes(new byte[] { 0xEE });
}

[TestFixture]
public class PendingSessionBehaviorTests
{
    [Test]
    public void ApproveAndRespond_SetsApproved_And_ReturnsResponse()
    {
        var clock = new TestClock2 { UtcNow = DateTimeOffset.Parse("2025-05-01T00:00:00Z") };
        var inv = HandshakeInvitation.FromBytes(new byte[] { 1, 2, 3 });
        var pending = PendingSession.FromInvitationWithMetadata(
            PendingSessionId.NewId(),
            new PeerId(1),
            new ProtocolVersion(1),
            inv,
            requestCorrelationId: new RequestCorrelationId(Guid.NewGuid()),
            isRelayed: false,
            relayHostPeerId: null,
            inviterIdentityKey: null,
            callbackEndpointHost: null,
            callbackEndpointPort: null,
            clock,
            expiresAtUtc: clock.UtcNow.AddHours(1));

        var resp = pending.ApproveAndRespond(new NoopCrypto(), new Mock<IKeyStore>().Object);

        pending.State.Should().Be(ApprovalState.Approved);
        resp.ToArray().Should().Equal(new byte[] { 0xEE });
    }

    [Test]
    public void AutoRespond_Allows_WhenPolicyPermits()
    {
        var clock = new TestClock2 { UtcNow = DateTimeOffset.Parse("2025-05-01T00:00:00Z") };
        var inv = HandshakeInvitation.FromBytes(new byte[] { 4, 5, 6 });
        var pending = PendingSession.FromInvitationWithMetadata(
            PendingSessionId.NewId(),
            new PeerId(1),
            new ProtocolVersion(1),
            inv,
            requestCorrelationId: new RequestCorrelationId(Guid.NewGuid()),
            isRelayed: false,
            relayHostPeerId: null,
            inviterIdentityKey: null,
            callbackEndpointHost: null,
            callbackEndpointPort: null,
            clock,
            expiresAtUtc: clock.UtcNow.AddHours(1));

        var resp = pending.AutoRespond(new NoopCrypto(), new Mock<IKeyStore>().Object, new ApprovalPolicy(allowAutoRespond: true));

        pending.State.Should().Be(ApprovalState.AutoResponded);
        resp.ToArray().Should().Equal(new byte[] { 0xEE });
    }

    [Test]
    public void AutoRespond_Denied_WhenPolicyForbids()
    {
        var clock = new TestClock2 { UtcNow = DateTimeOffset.Parse("2025-05-01T00:00:00Z") };
        var inv = HandshakeInvitation.FromBytes(new byte[] { 7, 8, 9 });
        var pending = PendingSession.FromInvitationWithMetadata(
            PendingSessionId.NewId(),
            new PeerId(1),
            new ProtocolVersion(1),
            inv,
            requestCorrelationId: new RequestCorrelationId(Guid.NewGuid()),
            isRelayed: false,
            relayHostPeerId: null,
            inviterIdentityKey: null,
            callbackEndpointHost: null,
            callbackEndpointPort: null,
            clock,
            expiresAtUtc: clock.UtcNow.AddHours(1));

        Action act = () => pending.AutoRespond(new NoopCrypto(), new Mock<IKeyStore>().Object, new ApprovalPolicy(allowAutoRespond: false));
        act.Should().Throw<InvalidOperationException>();
        pending.State.Should().Be(ApprovalState.AwaitingApproval);
    }

    [Test]
    public void ApproveAndRespond_Throws_WhenRejected()
    {
        // Arrange
        var clock = new TestClock2 { UtcNow = DateTimeOffset.Parse("2025-05-01T00:00:00Z") };
        var pending = PendingSession.FromInvitationWithMetadata(
            PendingSessionId.NewId(),
            new PeerId(1),
            new ProtocolVersion(1),
            HandshakeInvitation.FromBytes(new byte[] { 1 }),
            requestCorrelationId: new RequestCorrelationId(Guid.NewGuid()),
            isRelayed: false,
            relayHostPeerId: null,
            inviterIdentityKey: null,
            callbackEndpointHost: null,
            callbackEndpointPort: null,
            clock,
            expiresAtUtc: clock.UtcNow.AddMinutes(5));
        pending.Reject();

        // Act
        Action act = () => pending.ApproveAndRespond(new NoopCrypto(), new Mock<IKeyStore>().Object);

        // Assert
        act.Should().Throw<InvalidOperationException>();
    }

    [Test]
    public void ApproveAndRespond_Throws_WhenExpired()
    {
        // Arrange
        var clock = new TestClock2 { UtcNow = DateTimeOffset.Parse("2025-05-01T00:00:00Z") };
        var pending = PendingSession.FromInvitationWithMetadata(
            PendingSessionId.NewId(),
            new PeerId(1),
            new ProtocolVersion(1),
            HandshakeInvitation.FromBytes(new byte[] { 2 }),
            requestCorrelationId: new RequestCorrelationId(Guid.NewGuid()),
            isRelayed: false,
            relayHostPeerId: null,
            inviterIdentityKey: null,
            callbackEndpointHost: null,
            callbackEndpointPort: null,
            clock,
            expiresAtUtc: clock.UtcNow);
        pending.Expire(clock.UtcNow);

        // Act
        Action act = () => pending.ApproveAndRespond(new NoopCrypto(), new Mock<IKeyStore>().Object);

        // Assert
        act.Should().Throw<InvalidOperationException>();
    }

    [Test]
    public void AutoRespond_Throws_WhenRejected_AndPolicyAllows()
    {
        // Arrange
        var clock = new TestClock2 { UtcNow = DateTimeOffset.Parse("2025-05-01T00:00:00Z") };
        var pending = PendingSession.FromInvitationWithMetadata(
            PendingSessionId.NewId(),
            new PeerId(1),
            new ProtocolVersion(1),
            HandshakeInvitation.FromBytes(new byte[] { 3 }),
            requestCorrelationId: new RequestCorrelationId(Guid.NewGuid()),
            isRelayed: false,
            relayHostPeerId: null,
            inviterIdentityKey: null,
            callbackEndpointHost: null,
            callbackEndpointPort: null,
            clock,
            expiresAtUtc: clock.UtcNow.AddHours(1));
        pending.Reject();

        // Act
        Action act = () => pending.AutoRespond(new NoopCrypto(), new Mock<IKeyStore>().Object, new ApprovalPolicy(allowAutoRespond: true));

        // Assert
        act.Should().Throw<InvalidOperationException>();
    }

    [Test]
    public void AutoRespond_Throws_WhenExpired_AndPolicyAllows()
    {
        // Arrange
        var clock = new TestClock2 { UtcNow = DateTimeOffset.Parse("2025-05-01T00:00:00Z") };
        var pending = PendingSession.FromInvitationWithMetadata(
            PendingSessionId.NewId(),
            new PeerId(1),
            new ProtocolVersion(1),
            HandshakeInvitation.FromBytes(new byte[] { 4 }),
            requestCorrelationId: new RequestCorrelationId(Guid.NewGuid()),
            isRelayed: false,
            relayHostPeerId: null,
            inviterIdentityKey: null,
            callbackEndpointHost: null,
            callbackEndpointPort: null,
            clock,
            expiresAtUtc: clock.UtcNow);
        pending.Expire(clock.UtcNow);

        // Act
        Action act = () => pending.AutoRespond(new NoopCrypto(), new Mock<IKeyStore>().Object, new ApprovalPolicy(allowAutoRespond: true));

        // Assert
        act.Should().Throw<InvalidOperationException>();
    }

    [Test]
    public void Reject_Throws_WhenAlreadyApproved()
    {
        // Arrange
        var clock = new TestClock2 { UtcNow = DateTimeOffset.Parse("2025-05-01T00:00:00Z") };
        var pending = PendingSession.FromInvitationWithMetadata(
            PendingSessionId.NewId(),
            new PeerId(1),
            new ProtocolVersion(1),
            HandshakeInvitation.FromBytes(new byte[] { 5 }),
            requestCorrelationId: new RequestCorrelationId(Guid.NewGuid()),
            isRelayed: false,
            relayHostPeerId: null,
            inviterIdentityKey: null,
            callbackEndpointHost: null,
            callbackEndpointPort: null,
            clock,
            expiresAtUtc: clock.UtcNow.AddHours(1));
        pending.ApproveAndRespond(new NoopCrypto(), new Mock<IKeyStore>().Object);

        // Act
        Action act = () => pending.Reject();

        // Assert
        act.Should().Throw<InvalidOperationException>();
    }

    [Test]
    public void Reject_Throws_WhenAlreadyAutoResponded()
    {
        // Arrange
        var clock = new TestClock2 { UtcNow = DateTimeOffset.Parse("2025-05-01T00:00:00Z") };
        var pending = PendingSession.FromInvitationWithMetadata(
            PendingSessionId.NewId(),
            new PeerId(1),
            new ProtocolVersion(1),
            HandshakeInvitation.FromBytes(new byte[] { 6 }),
            requestCorrelationId: new RequestCorrelationId(Guid.NewGuid()),
            isRelayed: false,
            relayHostPeerId: null,
            inviterIdentityKey: null,
            callbackEndpointHost: null,
            callbackEndpointPort: null,
            clock,
            expiresAtUtc: clock.UtcNow.AddHours(1));
        pending.AutoRespond(new NoopCrypto(), new Mock<IKeyStore>().Object, new ApprovalPolicy(allowAutoRespond: true));

        // Act
        Action act = () => pending.Reject();

        // Assert
        act.Should().Throw<InvalidOperationException>();
    }

    [Test]
    public void Reject_SetsRejected()
    {
        var clock = new TestClock2();
        var pending = PendingSession.FromInvitationWithMetadata(
            PendingSessionId.NewId(),
            new PeerId(1),
            new ProtocolVersion(1),
            HandshakeInvitation.FromBytes(new byte[] { 1 }),
            requestCorrelationId: new RequestCorrelationId(Guid.NewGuid()),
            isRelayed: false,
            relayHostPeerId: null,
            inviterIdentityKey: null,
            callbackEndpointHost: null,
            callbackEndpointPort: null,
            clock);
        pending.Reject();
        pending.State.Should().Be(ApprovalState.Rejected);
    }

    [Test]
    public void Expire_SetsExpired_WhenPast()
    {
        var clock = new TestClock2 { UtcNow = DateTimeOffset.Parse("2025-05-01T00:00:00Z") };
        var pending = PendingSession.FromInvitationWithMetadata(
            PendingSessionId.NewId(),
            new PeerId(1),
            new ProtocolVersion(1),
            HandshakeInvitation.FromBytes(new byte[] { 1 }),
            requestCorrelationId: new RequestCorrelationId(Guid.NewGuid()),
            isRelayed: false,
            relayHostPeerId: null,
            inviterIdentityKey: null,
            callbackEndpointHost: null,
            callbackEndpointPort: null,
            clock,
            expiresAtUtc: clock.UtcNow);

        pending.Expire(clock.UtcNow);
        pending.State.Should().Be(ApprovalState.Expired);
    }
}
