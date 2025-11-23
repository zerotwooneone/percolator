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
        var root = new RootKey(new byte[32]);
        var (initiator, responder) = CryptoTestBootstrap.CreatePairedStates(root);
        var receiver = SecureSession.Create(
            SessionId.NewId(),
            PeerId.NewId(),
            new ProtocolVersion(1),
            responder,
            crypto,
            clock);
        var sender = SecureSession.Create(
            SessionId.NewId(),
            PeerId.NewId(),
            new ProtocolVersion(1),
            initiator,
            crypto,
            clock);

        var good = sender.Encrypt(new Plaintext(new byte[] { 0x11 }), clock);

        // Tamper header: empty ratchet key is invalid (test first to avoid replay)
        var (hdr, ctr, prev) = good.GetHeader();
        var tampered = SessionRatchetMessage.Create(new RatchetEphemeralKey(Array.Empty<byte>()), ctr, prev, good.GetCiphertext());
        Action act = () => receiver.Decrypt(tampered, clock);
        act.Should().Throw<InvalidOperationException>();

        // Sanity: non-tampered decrypt then succeeds
        var pt = receiver.Decrypt(good, clock);
        pt.Value.Should().NotBeNull();
    }
}
