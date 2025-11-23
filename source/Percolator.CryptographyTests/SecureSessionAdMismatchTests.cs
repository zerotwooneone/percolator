using System;
using FluentAssertions;
using NUnit.Framework;
using Percolator.Cryptography;
using Percolator.Cryptography.Primitives;

namespace Percolator.CryptographyTests;

file sealed class TestClock_ADMismatch : IClock
{
    public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.Parse("2025-08-10T00:30:00Z");
}

[TestFixture]
public class SecureSessionAdMismatchTests
{
    [Test]
    public void Decrypt_Throws_On_AD_Mismatch()
    {
        var clock = new TestClock_ADMismatch();
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

        var adSender = new AssociatedData(new byte[] { 0xA1 });
        var adReceiver = new AssociatedData(new byte[] { 0xB2 });
        var good = sender.Encrypt(new Plaintext(new byte[] { 0x33 }), adSender, clock);

        Action act = () => receiver.Decrypt(good, adReceiver, clock);
        act.Should().Throw<Exception>();
    }
}
