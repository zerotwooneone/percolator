using System;
using FluentAssertions;
using NUnit.Framework;
using Percolator.Cryptography;
using Percolator.Cryptography.Primitives;

namespace Percolator.CryptographyTests;

file sealed class TestClock13 : IClock
{
    public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.Parse("2025-08-08T00:00:00Z");
}

[TestFixture]
public class SecureSessionSkippedKeysTests
{
    [Test]
    public void OutOfOrder_Is_Buffered_And_Later_Decrypted_When_Missing_Arrives()
    {
        var clock = new TestClock13();
        var crypto = new AeadSessionCrypto();
        var receiver = SecureSession.Create(
            SessionId.NewId(),
            PeerId.NewId(),
            new ProtocolVersion(1),
            new RatchetState(new RootKey(new byte[32]), null, 0, null, 0, 0, null, null, 1000),
            crypto,
            clock);
        var sender = SecureSession.Create(
            SessionId.NewId(),
            PeerId.NewId(),
            new ProtocolVersion(1),
            new RatchetState(new RootKey(new byte[32]), null, 0, null, 0, 0, null, null, 1000),
            crypto,
            clock);

        // Receive counter 1 before 0 -> should NOT throw (buffer it)
        var m1 = sender.Encrypt(new Plaintext(new byte[] { 0xAA }), clock);
        // Simulate out-of-order by first encrypting another message so sender's counter advances;
        // but we will deliver m1 (counter 0) before m0 below
        var m0 = sender.Encrypt(new Plaintext(new byte[] { 0x01 }), clock);
        Action actOutOfOrder = () => receiver.Decrypt(m1, clock);
        actOutOfOrder.Should().NotThrow(); // RED currently: we throw

        // Now receive counter 0 -> should decrypt
        var p0 = receiver.Decrypt(m0, clock);
        p0.Value.Should().NotBeNull();

        // And decrypt previously buffered counter 1 now
        var p1 = receiver.Decrypt(m1, clock);
        p1.Value.Should().NotBeNull();
    }
}
