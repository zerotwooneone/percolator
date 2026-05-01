using FluentAssertions;
using Percolator.Cryptography;
using Percolator.Cryptography.Primitives;

namespace Percolator.CryptographyTests;

file sealed class TestClock_Replay : IClock
{
    public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.Parse("2025-08-09T00:00:00Z");
}

[TestFixture]
public class SecureSessionReplayTests
{
    [Test]
    public void DoubleDecrypt_SameMessage_Is_Rejected()
    {
        var clock = new TestClock_Replay();
        var crypto = new AeadSessionCrypto();
        var root = RootKey.FromBytes(new byte[32]);
        var (initiator, responder) = CryptoTestBootstrap.CreatePairedStates(root);
        var receiver = SecureSession.Create(SessionId.NewId(), PeerId.NewId(), new ProtocolVersion(1), responder, crypto, clock);
        var sender = SecureSession.Create(SessionId.NewId(), PeerId.NewId(), new ProtocolVersion(1), initiator, crypto, clock);

        var msg = sender.Encrypt(Plaintext.FromBytes(new byte[] { 0x55 }), clock);
        var p0 = receiver.Decrypt(msg, clock);
        p0.ToArray().Should().NotBeNull();

        Action actReplay = () => receiver.Decrypt(msg, clock);
        actReplay.Should().Throw<InvalidOperationException>();
    }
}
