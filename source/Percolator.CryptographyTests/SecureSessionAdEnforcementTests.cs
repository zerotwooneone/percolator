using System;
using FluentAssertions;
using NUnit.Framework;
using Percolator.Cryptography;
using Percolator.Cryptography.Primitives;

namespace Percolator.CryptographyTests;

file sealed class TestClock8 : IClock
{
    public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.Parse("2025-08-03T00:00:00Z");
}

[TestFixture]
public class SecureSessionAdEnforcementTests
{
    [Test]
    public void Decrypt_Throws_On_Tampered_HeaderKey()
    {
        var clock = new TestClock8();
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

        var good = sender.Encrypt(new Plaintext(new byte[] { 0x11 }), clock);

        // Sanity: non-tampered decrypt succeeds
        var pt = receiver.Decrypt(good, clock);
        pt.Value.Should().NotBeNull();

        // Tamper header: empty ratchet key is invalid
        var (hdr, ctr, prev) = good.GetHeader();
        var tampered = SessionRatchetMessage.Create(new RatchetEphemeralKey(Array.Empty<byte>()), ctr, prev, good.GetCiphertext());
        Action act = () => receiver.Decrypt(tampered, clock);
        act.Should().Throw<ArgumentException>();
    }
}
