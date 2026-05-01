using FluentAssertions;
using Percolator.Cryptography;
using Percolator.Cryptography.Primitives;

namespace Percolator.CryptographyTests;

file sealed class TestClock7 : IClock
{
    public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.Parse("2025-08-02T00:00:00Z");
}

[TestFixture]
public class SecureSessionCounterTests
{
    [Test]
    public void Encrypt_Increments_Message_Counter_Monotonically()
    {
        var clock = new TestClock7();
        var crypto = new AeadSessionCrypto();
        var s = SecureSession.Create(
            SessionId.NewId(),
            PeerId.NewId(),
            new ProtocolVersion(1),
            CryptoTestBootstrap.CreateBootstrappedState(RootKey.FromBytes(new byte[32])),
            crypto,
            clock);

        var m1 = s.Encrypt(Plaintext.FromBytes(new byte[] { 1 }), clock);
        var m2 = s.Encrypt(Plaintext.FromBytes(new byte[] { 2 }), clock);

        var (_, c1, _) = m1.GetHeader();
        var (_, c2, _) = m2.GetHeader();

        c1.Should().BeLessThan(c2);
        c1.Should().Be(0UL);
        c2.Should().Be(1UL);
    }
}
