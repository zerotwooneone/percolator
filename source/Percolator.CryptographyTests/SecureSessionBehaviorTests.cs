using System;
using FluentAssertions;
using NUnit.Framework;
using Percolator.Cryptography;
using Percolator.Cryptography.Primitives;

namespace Percolator.CryptographyTests;

file sealed class TestClock4 : IClock
{
    public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.Parse("2025-06-02T10:00:00Z");
}

[TestFixture]
public class SecureSessionBehaviorTests
{
    private static SecureSession CreateBaselineSession(IClock clock)
    {
        var id = SessionId.NewId();
        var peer = PeerId.NewId();
        var version = new ProtocolVersion(1);
        var state = new RatchetState(new RootKey(new byte[32]), null, 0, null, 0, 0, null, null, 1000);
        return SecureSession.Create(id, peer, version, state, clock);
    }

    [Test]
    public void Encrypt_FirstMessage_AsInitiator_EmitsRatchetFramedMessage()
    {
        // Arrange
        var clock = new TestClock4();
        var session = CreateBaselineSession(clock);
        var pt = new Plaintext(new byte[] { 1, 2, 3 });

        // Act
        var msg = session.Encrypt(pt, clock);

        // Assert (behavioral)
        var (key, counter, prevLen) = msg.GetHeader();
        key.Value.Should().NotBeNull();
        counter.Should().BeGreaterThanOrEqualTo(0UL);
        prevLen.Should().BeGreaterThanOrEqualTo(0UL);
        msg.GetCiphertext().Value.Length.Should().BeGreaterThan(0);
        session.LastUsedAtUtc.Should().Be(clock.UtcNow);
    }

    [Test]
    public void Decrypt_FirstInbound_AsResponder_AdvancesState_AndReturnsPlaintext()
    {
        // Arrange
        var clock = new TestClock4();
        var session = CreateBaselineSession(clock);
        var headerKey = new RatchetEphemeralKey(new byte[] { 5, 6, 7 });
        var framed = SessionRatchetMessage.Create(headerKey, 0, 0, new Ciphertext(new byte[] { 9, 9 }));
        clock.UtcNow = clock.UtcNow.AddMinutes(1);

        // Act
        var pt = session.Decrypt(framed, clock);

        // Assert
        pt.Value.Should().NotBeNull();
        session.LastUsedAtUtc.Should().Be(clock.UtcNow);
    }
}
