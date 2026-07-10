using FluentAssertions;
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
        var root = RootKey.FromBytes(new byte[32]);
        var (initiator, responder) = CryptoTestBootstrap.CreatePairedStates(root);
        var receiver = SecureSession.Create(
            SessionId.NewId(),
            new CryptoPeerId(1),
            new ProtocolVersion(1),
            responder,
            crypto,
            clock);
        var sender = SecureSession.Create(
            SessionId.NewId(),
            new CryptoPeerId(1),
            new ProtocolVersion(1),
            initiator,
            crypto,
            clock);

        var good = sender.Encrypt(Plaintext.FromBytes(new byte[] { 0x21, 0x22, 0x23 }), clock);
        var (hdr, ctr, prev) = good.GetHeader();
        var tampered = SessionRatchetMessage.Create(hdr, ctr, prev, Ciphertext.FromBytes(new byte[] { 0x01 }));

        Action act = () => receiver.Decrypt(tampered, clock);
        act.Should().Throw<InvalidOperationException>();
    }
}
