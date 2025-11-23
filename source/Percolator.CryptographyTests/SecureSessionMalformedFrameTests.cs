using System;
using FluentAssertions;
using NUnit.Framework;
using Percolator.Cryptography;
using Percolator.Cryptography.Primitives;

namespace Percolator.CryptographyTests;

file sealed class TestClock_Malformed : IClock
{
    public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.Parse("2025-08-10T00:00:00Z");
}

[TestFixture]
public class SecureSessionMalformedFrameTests
{
    [Test]
    public void Decrypt_Throws_On_Truncated_Ciphertext()
    {
        var clock = new TestClock_Malformed();
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

        var good = sender.Encrypt(new Plaintext(new byte[] { 0x21, 0x22, 0x23 }), clock);
        var (hdr, ctr, prev) = good.GetHeader();
        var tampered = SessionRatchetMessage.Create(hdr, ctr, prev, new Ciphertext(new byte[] { 0x01 }));

        Action act = () => receiver.Decrypt(tampered, clock);
        act.Should().Throw<InvalidOperationException>();
    }
}
