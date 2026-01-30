using System;
using FluentAssertions;
using NUnit.Framework;
using Percolator.Cryptography;
using Percolator.Cryptography.Primitives;

namespace Percolator.CryptographyTests;

file sealed class TestClock : IClock
{
    public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.Parse("2025-01-01T00:00:00Z");
}

[TestFixture]
public class AggregateSkeletonTests
{
    [Test]
    public void SecureSession_Construct_Sets_Invariants()
    {
        var clock = new TestClock { UtcNow = DateTimeOffset.Parse("2025-03-01T12:00:00Z") };
        var id = SessionId.NewId();
        var peer = PeerId.NewId();
        var version = new ProtocolVersion(1);
        var state = new RatchetState(new RootKey(new byte[32]), null, 0, null, 0, 0, null, null, 1000);

        var crypto = new AeadSessionCrypto();
        var s = SecureSession.Create(id, peer, version, state, crypto, clock);

        s.Id.Should().Be(id);
        s.RemotePeerId.Should().Be(peer);
        s.ProtocolVersion.Should().Be(version);
        s.State.Should().NotBeNull();
        s.CreatedAtUtc.Should().Be(clock.UtcNow);
        s.LastUsedAtUtc.Should().Be(clock.UtcNow);
    }

    [Test]
    public void SecureSession_NullArguments_Throw()
    {
        var clock = new TestClock();
        var id = SessionId.NewId();
        var peer = PeerId.NewId();
        var version = new ProtocolVersion(1);
        var crypto = new AeadSessionCrypto();
        Assert.Throws<ArgumentNullException>(() => SecureSession.Create(id, peer, version, null!, crypto, clock));
        Assert.Throws<ArgumentNullException>(() => SecureSession.Create(id, peer, version, new RatchetState(new RootKey(new byte[32]), null, 0, null, 0, 0, null, null, 1000), crypto, null!));
        Assert.Throws<ArgumentNullException>(() => SecureSession.Create(id, peer, version, new RatchetState(new RootKey(new byte[32]), null, 0, null, 0, 0, null, null, 1000), null!, clock));
    }

    [Test]
    public void PendingSession_FromInvitation_Sets_Invariants()
    {
        var clock = new TestClock { UtcNow = DateTimeOffset.Parse("2025-04-01T08:30:00Z") };
        var id = PendingSessionId.NewId();
        var peer = PeerId.NewId();
        var version = new ProtocolVersion(2);
        var invitation = new HandshakeInvitation(new byte[] { 1, 2, 3 });

        var p = PendingSession.FromInvitationWithMetadata(
            id,
            peer,
            version,
            invitation,
            requestCorrelationId: new RequestCorrelationId(Guid.NewGuid()),
            isRelayed: false,
            inviterIdentityKey: null,
            callbackEndpointHost: null,
            callbackEndpointPort: null,
            clock,
            expiresAtUtc: clock.UtcNow.AddHours(1));

        p.Id.Should().Be(id);
        p.RemotePeerId.Should().Be(peer);
        p.ProtocolVersion.Should().Be(version);
        p.Invitation.Should().Be(invitation);
        p.State.Should().Be(ApprovalState.AwaitingApproval);
        p.CreatedAtUtc.Should().Be(clock.UtcNow);
        p.ExpiresAtUtc.Should().Be(clock.UtcNow.AddHours(1));
    }
}
