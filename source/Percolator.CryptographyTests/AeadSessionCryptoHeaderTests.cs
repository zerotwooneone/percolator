using System.Security.Cryptography;
using FluentAssertions;
using Percolator.Cryptography;
using Percolator.Cryptography.Primitives;

namespace Percolator.CryptographyTests;

[TestFixture]
public class AeadSessionCryptoHeaderTests
{
    [Test]
    public void DR_Encrypt_Emits_Valid_P256_SPKI_HeaderKey()
    {
        var crypto = new AeadSessionCrypto();
        var state = CryptoTestBootstrap.CreateBootstrappedState(RootKey.FromBytes(new byte[32]));
        var ad = AssociatedData.None;
        var pt = Plaintext.FromBytes(new byte[] { 0x10 });

        var (ct, header, _) = crypto.DR_Encrypt(state, pt, ad, 0, 0);
        header.ToArray().Should().NotBeNull();
        header.ToArray().Length.Should().BeGreaterThan(0);

        int read;
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportSubjectPublicKeyInfo(header.ToArray(), out read);
        read.Should().Be(header.ToArray().Length);

        var parms = ecdsa.ExportParameters(false);
        parms.Curve.Oid.FriendlyName.Should().Be("nistP256");
    }

    [Test]
    public void Initiator_And_Responder_First_HeaderKeys_Differ()
    {
        var clock = new TestClock_FirstHeaders();
        var crypto = new AeadSessionCrypto();
        var initiator = SecureSession.Create(SessionId.NewId(), new CryptoPeerId(1), new ProtocolVersion(1), CryptoTestBootstrap.CreateBootstrappedState(RootKey.FromBytes(new byte[32])), crypto, clock);
        var responder = SecureSession.Create(SessionId.NewId(), new CryptoPeerId(1), new ProtocolVersion(1), CryptoTestBootstrap.CreateBootstrappedState(RootKey.FromBytes(new byte[32])), crypto, clock);

        var mInit = initiator.Encrypt(Plaintext.FromBytes(new byte[] { 0x01 }), clock);
        var mResp = responder.Encrypt(Plaintext.FromBytes(new byte[] { 0x02 }), clock);

        var (k1, _, _) = mInit.GetHeader();
        var (k2, _, _) = mResp.GetHeader();
        k1.ToArray().Should().NotBeEquivalentTo(k2.ToArray());
    }
}

file sealed class TestClock_FirstHeaders : IClock
{
    public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.Parse("2025-08-11T00:00:00Z");
}
