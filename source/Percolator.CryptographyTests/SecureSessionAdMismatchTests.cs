using FluentAssertions;
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

        var adSender = new AssociatedData(new byte[] { 0xA1 });
        var adReceiver = new AssociatedData(new byte[] { 0xB2 });
        var good = sender.Encrypt(new Plaintext(new byte[] { 0x33 }), adSender, clock);

        Action act = () => receiver.Decrypt(good, adReceiver, clock);
        act.Should().Throw<Exception>();
    }
}
