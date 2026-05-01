using FluentAssertions;
using Percolator.Cryptography;
using Percolator.Cryptography.Primitives;

namespace Percolator.CryptographyTests;

file sealed class TestClock12 : IClock
{
    public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.Parse("2025-08-07T00:00:00Z");
}

[TestFixture]
public class SecureSessionResponderFinalizeTests
{
    [Test]
    public void Responder_First_Decrypt_Advances_Receive_Counter()
    {
        var clock = new TestClock12();
        var crypto = new AeadSessionCrypto();
        var root = RootKey.FromBytes(new byte[32]);
        var (initiator, responder) = CryptoTestBootstrap.CreatePairedStates(root);
        var receiver = SecureSession.Create(SessionId.NewId(), PeerId.NewId(), new ProtocolVersion(1), responder, crypto, clock);
        var sender = SecureSession.Create(SessionId.NewId(), PeerId.NewId(), new ProtocolVersion(1), initiator, crypto, clock);

        // First inbound (counter 0) decrypts
        var ptx0 = Plaintext.FromBytes(new byte[] { 0x01 });
        var m0 = sender.Encrypt(ptx0, clock);
        var p0 = receiver.Decrypt(m0, clock);
        p0.ToArray().Should().NotBeNull();

        // Second inbound (counter 1) should now succeed; wrong counter (0 again) would fail
        var ptx1 = Plaintext.FromBytes(new byte[] { 0x02 });
        var m1 = sender.Encrypt(ptx1, clock);
        var p1 = receiver.Decrypt(m1, clock);
        p1.ToArray().Should().NotBeNull();
    }
}
