using FluentAssertions;
using Percolator.Cryptography;
using Percolator.Cryptography.Primitives;

namespace Percolator.CryptographyTests;

file sealed class TestClock10 : IClock
{
    public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.Parse("2025-08-05T00:00:00Z");
}

[TestFixture]
public class SecureSessionDhAsymmetryTests
{
    [Test]
    public void Initiator_First_Encrypt_Uses_NonTrivial_HeaderKey()
    {
        var clock = new TestClock10();
        var crypto = new AeadSessionCrypto();
        var s = SecureSession.Create(
            SessionId.NewId(),
            new CryptoPeerId(1),
            new ProtocolVersion(1),
            CryptoTestBootstrap.CreateBootstrappedState(RootKey.FromBytes(new byte[32])),
            crypto,
            clock);

        var msg = s.Encrypt(Plaintext.FromBytes(new byte[] { 0x01 }), clock);
        var (preKey, _, _) = msg.GetHeader();

        preKey.ToArray().Should().NotBeNull();
        preKey.ToArray().Length.Should().BeGreaterThanOrEqualTo(32); // expect real DH pubkey length
        preKey.ToArray()[0].Should().NotBe(0x01); // not the trivial placeholder key
    }
}
