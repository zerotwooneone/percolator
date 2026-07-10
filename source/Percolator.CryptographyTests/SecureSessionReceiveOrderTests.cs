using FluentAssertions;
using Percolator.Cryptography;
using Percolator.Cryptography.Primitives;

namespace Percolator.CryptographyTests;

file sealed class TestClock11 : IClock
{
    public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.Parse("2025-08-06T00:00:00Z");
}

[TestFixture]
public class SecureSessionReceiveOrderTests
{
    [Test]
    public void Decrypt_Does_Not_Throw_On_OutOfOrder_Receive_When_Buffering_Enabled()
    {
        var clock = new TestClock11();
        var crypto = new AeadSessionCrypto();
        var s = SecureSession.Create(
            SessionId.NewId(),
            new CryptoPeerId(1),
            new ProtocolVersion(1),
            new RatchetState(RootKey.FromBytes(new byte[32]), null, 0, null, 0, 0, null, null, 1000),
            crypto,
            clock);

        // Simulate receiving a future counter (2) before 0 or 1
        var msgOutOfOrder = SessionRatchetMessage.Create(RatchetEphemeralKey.FromBytes(new byte[64]), 2, 0, Ciphertext.FromBytes(new byte[] { 0x33 }));
        Action act = () => s.Decrypt(msgOutOfOrder, clock);
        act.Should().NotThrow();
    }
}
