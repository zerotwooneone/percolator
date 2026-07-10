using FluentAssertions;
using Percolator.Cryptography;
using Percolator.Cryptography.Primitives;

namespace Percolator.CryptographyTests;

file sealed class TestClock14 : IClock
{
    public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.Parse("2025-08-09T00:00:00Z");
}

[TestFixture]
public class SecureSessionSkippedKeyLimitTests
{
    [Test]
    public void Buffer_Limit_Enforced_On_OutOfOrder_Messages()
    {
        var clock = new TestClock14();
        var crypto = new AeadSessionCrypto();
        var s = SecureSession.Create(
            SessionId.NewId(),
            new CryptoPeerId(1),
            new ProtocolVersion(1),
            new RatchetState(RootKey.FromBytes(new byte[32]), null, 0, null, 0, 0, null, null, skippedKeyLimit: 1),
            crypto,
            clock);

        // First out-of-order (counter 1) is buffered
        var m1 = SessionRatchetMessage.Create(RatchetEphemeralKey.FromBytes(new byte[64]), 1, 0, Ciphertext.FromBytes(new byte[] { 0xAA }));
        s.Decrypt(m1, clock);

        // Second out-of-order (counter 2) should exceed limit and throw
        var m2 = SessionRatchetMessage.Create(RatchetEphemeralKey.FromBytes(new byte[64]), 2, 0, Ciphertext.FromBytes(new byte[] { 0xBB }));
        Action act = () => s.Decrypt(m2, clock);
        act.Should().Throw<InvalidOperationException>();
    }
}
