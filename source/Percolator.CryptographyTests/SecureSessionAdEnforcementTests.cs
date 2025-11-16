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
        var s = SecureSession.Create(
            SessionId.NewId(),
            PeerId.NewId(),
            new ProtocolVersion(1),
            new RatchetState(new RootKey(new byte[32]), null, 0, null, 0, 0, null, null, 1000),
            clock);

        var ct = new Ciphertext(new byte[] { 0x11 });
        var good = SessionRatchetMessage.Create(new RatchetEphemeralKey(new byte[] { 0xAA }), 0, 0, ct);

        // Sanity: non-tampered decrypt succeeds
        var pt = s.Decrypt(good, clock);
        pt.Value.Should().NotBeNull();

        // Tamper header: empty ratchet key is invalid
        var tampered = SessionRatchetMessage.Create(new RatchetEphemeralKey(Array.Empty<byte>()), 0, 0, ct);
        Action act = () => s.Decrypt(tampered, clock);
        act.Should().Throw<ArgumentException>();
    }
}
