using System;
using FluentAssertions;
using NUnit.Framework;
using Percolator.Cryptography;
using Percolator.Cryptography.Primitives;

namespace Percolator.CryptographyTests;

file sealed class TestClock12 : IClock
{
    public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.Parse("2025-08-07T00:00:00Z");
}

[TestFixture]
public class SecureSessionResponderFinalizeTests
{
    [Test]
    public void Responder_First_Decrypt_Advances_Receive_Counter()
    {
        var clock = new TestClock12();
        var s = SecureSession.Create(
            SessionId.NewId(),
            PeerId.NewId(),
            new ProtocolVersion(1),
            new RatchetState(new RootKey(new byte[32]), null, 0, null, 0, 0, null, null, 1000),
            clock);

        // First inbound (counter 0) decrypts
        var m0 = SessionRatchetMessage.Create(new RatchetEphemeralKey(new byte[32]), 0, 0, new Ciphertext(new byte[] { 0x01 }));
        var p0 = s.Decrypt(m0, clock);
        p0.Value.Should().NotBeNull();

        // Second inbound (counter 1) should now succeed; wrong counter (0 again) would fail
        var m1 = SessionRatchetMessage.Create(new RatchetEphemeralKey(new byte[32]), 1, 0, new Ciphertext(new byte[] { 0x02 }));
        var p1 = s.Decrypt(m1, clock);
        p1.Value.Should().NotBeNull();
    }
}
