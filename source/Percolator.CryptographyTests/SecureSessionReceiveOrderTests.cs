using System;
using FluentAssertions;
using NUnit.Framework;
using Percolator.Cryptography;
using Percolator.Cryptography.Primitives;

namespace Percolator.CryptographyTests;

file sealed class TestClock11 : IClock
{
    public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.Parse("2025-08-06T00:00:00Z");
}

[TestFixture]
public class SecureSessionReceiveOrderTests
{
    [Test]
    public void Decrypt_Does_Not_Throw_On_OutOfOrder_Receive_When_Buffering_Enabled()
    {
        var clock = new TestClock11();
        var s = SecureSession.Create(
            SessionId.NewId(),
            PeerId.NewId(),
            new ProtocolVersion(1),
            new RatchetState(new RootKey(new byte[32]), null, 0, null, 0, 0, null, null, 1000),
            clock);

        // Simulate receiving a future counter (2) before 0 or 1
        var msgOutOfOrder = SessionRatchetMessage.Create(new RatchetEphemeralKey(new byte[32]), 2, 0, new Ciphertext(new byte[] { 0x33 }));
        Action act = () => s.Decrypt(msgOutOfOrder, clock);
        act.Should().NotThrow();
    }
}
