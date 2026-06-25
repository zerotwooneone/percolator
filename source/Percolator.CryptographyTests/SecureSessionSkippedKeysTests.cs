using FluentAssertions;
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
        var root = RootKey.FromBytes(new byte[32]);
        var (initiator, responder) = CryptoTestBootstrap.CreatePairedStates(root);
        var receiver = SecureSession.Create(SessionId.NewId(), new PeerId(1), new ProtocolVersion(1), responder, crypto, clock);
        var sender = SecureSession.Create(SessionId.NewId(), new PeerId(1), new ProtocolVersion(1), initiator, crypto, clock);

        // Receive counter 1 before 0 -> should NOT throw (buffer it)
        // Produce two messages, where m0 has counter 0 and m1 has counter 1
        var m0 = sender.Encrypt(Plaintext.FromBytes(new byte[] { 0x01 }), clock);
        var m1 = sender.Encrypt(Plaintext.FromBytes(new byte[] { 0xAA }), clock);
        Action actOutOfOrder = () => receiver.Decrypt(m1, clock);
        actOutOfOrder.Should().NotThrow(); // RED currently: we throw

        // Now receive counter 0 -> should decrypt
        var p0 = receiver.Decrypt(m0, clock);
        p0.ToArray().Should().NotBeNull();

        // And decrypt previously buffered counter 1 now
        var p1 = receiver.Decrypt(m1, clock);
        p1.ToArray().Should().NotBeNull();
    }
}
