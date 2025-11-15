using System;
using FluentAssertions;
using NUnit.Framework;
using Percolator.Cryptography;
using Percolator.Cryptography.Primitives;

namespace Percolator.CryptographyTests;

file sealed class TestClock2 : IClock
{
    public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.UtcNow;
}

file sealed class NoopCrypto : ICryptoPrimitives { }
file sealed class NoopKeyStore : IKeyStore { }

[TestFixture]
public class PendingSessionBehaviorTests
{
    [Test]
    public void ApproveAndRespond_SetsApproved_And_ReturnsResponse()
    {
        var clock = new TestClock2 { UtcNow = DateTimeOffset.Parse("2025-05-01T00:00:00Z") };
        var inv = new HandshakeInvitation(new byte[] { 1, 2, 3 });
        var pending = PendingSession.FromInvitation(
            PendingSessionId.NewId(),
            PeerId.NewId(),
            new ProtocolVersion(1),
            inv,
            clock,
            expiresAtUtc: clock.UtcNow.AddHours(1));

        var resp = pending.ApproveAndRespond(new NoopCrypto(), new NoopKeyStore());

        pending.State.Should().Be(ApprovalState.Approved);
        resp.Value.Should().NotBeEmpty();
    }

    [Test]
    public void AutoRespond_Allows_WhenPolicyPermits()
    {
        var clock = new TestClock2 { UtcNow = DateTimeOffset.Parse("2025-05-01T00:00:00Z") };
        var inv = new HandshakeInvitation(new byte[] { 4, 5, 6 });
        var pending = PendingSession.FromInvitation(
            PendingSessionId.NewId(),
            PeerId.NewId(),
            new ProtocolVersion(1),
            inv,
            new ApprovalPolicy(allowAutoRespond: true),
            clock,
            expiresAtUtc: clock.UtcNow.AddHours(1));

        var resp = pending.AutoRespond(new NoopCrypto(), new NoopKeyStore());

        pending.State.Should().Be(ApprovalState.AutoResponded);
        resp.Value.Should().NotBeEmpty();
    }

    [Test]
    public void AutoRespond_Denied_WhenPolicyForbids()
    {
        var clock = new TestClock2 { UtcNow = DateTimeOffset.Parse("2025-05-01T00:00:00Z") };
        var inv = new HandshakeInvitation(new byte[] { 7, 8, 9 });
        var pending = PendingSession.FromInvitation(
            PendingSessionId.NewId(),
            PeerId.NewId(),
            new ProtocolVersion(1),
            inv,
            new ApprovalPolicy(allowAutoRespond: false),
            clock,
            expiresAtUtc: clock.UtcNow.AddHours(1));

        Action act = () => pending.AutoRespond(new NoopCrypto(), new NoopKeyStore());
        act.Should().Throw<InvalidOperationException>();
        pending.State.Should().Be(ApprovalState.AwaitingApproval);
    }

    [Test]
    public void Reject_SetsRejected()
    {
        var clock = new TestClock2();
        var pending = PendingSession.FromInvitation(
            PendingSessionId.NewId(), PeerId.NewId(), new ProtocolVersion(1), new HandshakeInvitation(new byte[] {1}), clock);
        pending.Reject();
        pending.State.Should().Be(ApprovalState.Rejected);
    }

    [Test]
    public void Expire_SetsExpired_WhenPast()
    {
        var clock = new TestClock2 { UtcNow = DateTimeOffset.Parse("2025-05-01T00:00:00Z") };
        var pending = PendingSession.FromInvitation(
            PendingSessionId.NewId(),
            PeerId.NewId(),
            new ProtocolVersion(1),
            new HandshakeInvitation(new byte[] { 1 }),
            clock,
            expiresAtUtc: clock.UtcNow);

        pending.Expire(clock);
        pending.State.Should().Be(ApprovalState.Expired);
    }
}
