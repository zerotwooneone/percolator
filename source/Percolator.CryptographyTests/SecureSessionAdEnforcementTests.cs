using FluentAssertions;
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
        var root = RootKey.FromBytes(new byte[32]);
        var (initiator, responder) = CryptoTestBootstrap.CreatePairedStates(root);
        var receiver = SecureSession.Create(
            SessionId.NewId(),
            new PeerId(1),
            new ProtocolVersion(1),
            responder,
            crypto,
            clock);
        var sender = SecureSession.Create(
            SessionId.NewId(),
            new PeerId(1),
            new ProtocolVersion(1),
            initiator,
            crypto,
            clock);

        var good = sender.Encrypt(Plaintext.FromBytes(new byte[] { 0x11 }), clock);

        // Tamper header: use a different ratchet key (same length, different content)
        var (hdr, ctr, prev) = good.GetHeader();
        var tampered = SessionRatchetMessage.Create(RatchetEphemeralKey.FromBytes(new byte[64]), ctr, prev, good.GetCiphertext());
        Action act = () => receiver.Decrypt(tampered, clock);
        act.Should().Throw<InvalidOperationException>();

        // Sanity: non-tampered decrypt then succeeds
        var pt = receiver.Decrypt(good, clock);
        pt.ToArray().Should().NotBeNull();
    }
}
