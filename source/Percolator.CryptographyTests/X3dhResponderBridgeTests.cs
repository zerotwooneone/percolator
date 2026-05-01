using FluentAssertions;
using Moq;
using Percolator.Cryptography;

namespace Percolator.CryptographyTests;

[TestFixture]
public class X3dhResponderBridgeTests
{
    [Test]
    public void DeriveResponder_WithOneTimePreKey_UsesOtkAndReturnsResult()
    {
        // ARRANGE
        var ik = RatchetIdentityKey.FromBytes(new byte[64]);
        var ek = RatchetEphemeralKey.FromBytes(new byte[64]);
        var invitation = new ParsedInvitation(ik, ek, "spk-1", "opk-1");

        var idPriv = PrivatePreKey.FromBytes(new byte[100]);
        var spkPriv = PrivatePreKey.FromBytes(new byte[100]);
        var otkPriv = PrivatePreKey.FromBytes(new byte[100]);
        var expected = new ResponderResult(SharedSecret.FromBytes(new byte[32]), UsedOneTimeKey: true);

        var ks = new Mock<IKeyStore>(MockBehavior.Strict);
        ks.Setup(k => k.GetIdentityPrivateKey()).Returns(idPriv);
        ks.Setup(k => k.GetSignedPreKeyPrivate("spk-1")).Returns(spkPriv);
        ks.Setup(k => k.TryGetOneTimePreKeyPrivate("opk-1")).Returns(otkPriv);

        var deriver = new Mock<IX3dhDeriver>(MockBehavior.Strict);
        deriver
            .Setup(d => d.DeriveResponder(ik, ek, idPriv, spkPriv, otkPriv))
            .Returns(expected);

        var bridge = new X3dhResponderBridge(deriver.Object, ks.Object);

        // ACT
        var result = bridge.DeriveResponder(invitation);

        // ASSERT
        result.Should().Be(expected);
    }

    [Test]
    public void DeriveResponder_WithoutOneTimePreKey_PassesNullOtk()
    {
        // ARRANGE
        var ik = RatchetIdentityKey.FromBytes(new byte[64]);
        var ek = RatchetEphemeralKey.FromBytes(new byte[64]);
        var invitation = new ParsedInvitation(ik, ek, "spk-2", null);

        var idPriv = PrivatePreKey.FromBytes(new byte[100]);
        var spkPriv = PrivatePreKey.FromBytes(new byte[100]);
        var expected = new ResponderResult(SharedSecret.FromBytes(new byte[32]), UsedOneTimeKey: false);

        var ks = new Mock<IKeyStore>(MockBehavior.Strict);
        ks.Setup(k => k.GetIdentityPrivateKey()).Returns(idPriv);
        ks.Setup(k => k.GetSignedPreKeyPrivate("spk-2")).Returns(spkPriv);

        var deriver = new Mock<IX3dhDeriver>(MockBehavior.Strict);
        deriver
            .Setup(d => d.DeriveResponder(ik, ek, idPriv, spkPriv, null))
            .Returns(expected);

        var bridge = new X3dhResponderBridge(deriver.Object, ks.Object);

        // ACT
        var result = bridge.DeriveResponder(invitation);

        // ASSERT
        result.Should().Be(expected);
    }
}
