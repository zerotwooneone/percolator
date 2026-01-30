using System;
using FluentAssertions;
using NUnit.Framework;
using Percolator.Cryptography;
using Percolator.Cryptography.Primitives;

namespace Percolator.CryptographyTests;

[TestFixture]
file sealed class TestClock3 : IClock
{
    public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.UtcNow;
}

[TestFixture]
public sealed class PendingSessionMetadataTests
{
    [Test]
    public void FromInvitationWithMetadata_Throws_WhenRelayedHasCallbackEndpoint()
    {
        var clock = new TestClock3 { UtcNow = DateTimeOffset.Parse("2025-05-01T00:00:00Z") };
        var act = () => PendingSession.FromInvitationWithMetadata(
            PendingSessionId.NewId(),
            PeerId.NewId(),
            new ProtocolVersion(1),
            new HandshakeInvitation(new byte[] { 1 }),
            requestCorrelationId: new RequestCorrelationId(Guid.NewGuid()),
            isRelayed: true,
            inviterIdentityKey: null,
            callbackEndpointHost: "127.0.0.1",
            callbackEndpointPort: 1234,
            clock);

        act.Should().Throw<InvalidOperationException>();
    }

    [Test]
    public void FromInvitationWithMetadata_Throws_WhenCallbackHostPortMismatch()
    {
        var clock = new TestClock3 { UtcNow = DateTimeOffset.Parse("2025-05-01T00:00:00Z") };

        var act1 = () => PendingSession.FromInvitationWithMetadata(
            PendingSessionId.NewId(),
            PeerId.NewId(),
            new ProtocolVersion(1),
            new HandshakeInvitation(new byte[] { 1 }),
            requestCorrelationId: new RequestCorrelationId(Guid.NewGuid()),
            isRelayed: false,
            inviterIdentityKey: null,
            callbackEndpointHost: "example.com",
            callbackEndpointPort: null,
            clock);

        var act2 = () => PendingSession.FromInvitationWithMetadata(
            PendingSessionId.NewId(),
            PeerId.NewId(),
            new ProtocolVersion(1),
            new HandshakeInvitation(new byte[] { 1 }),
            requestCorrelationId: new RequestCorrelationId(Guid.NewGuid()),
            isRelayed: false,
            inviterIdentityKey: null,
            callbackEndpointHost: null,
            callbackEndpointPort: 443,
            clock);

        act1.Should().Throw<InvalidOperationException>();
        act2.Should().Throw<InvalidOperationException>();
    }
}
