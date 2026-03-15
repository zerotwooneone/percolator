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
        var ik = new RatchetIdentityKey(new byte[] { 0x01, 0x02 });
        var ek = new RatchetEphemeralKey(new byte[] { 0x03, 0x04 });
        var invitation = new ParsedInvitation(ik, ek, "spk-1", "opk-1");

        var idPriv = new PrivatePreKey(new byte[] { 0x10 });
        var spkPriv = new PrivatePreKey(new byte[] { 0x11 });
        var otkPriv = new PrivatePreKey(new byte[] { 0x12 });
        var expected = new ResponderResult(new SharedSecret(new byte[] { 0xAA }), UsedOneTimeKey: true);

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
        var ik = new RatchetIdentityKey(new byte[] { 0x05, 0x06 });
        var ek = new RatchetEphemeralKey(new byte[] { 0x07, 0x08 });
        var invitation = new ParsedInvitation(ik, ek, "spk-2", null);

        var idPriv = new PrivatePreKey(new byte[] { 0x20 });
        var spkPriv = new PrivatePreKey(new byte[] { 0x21 });
        var expected = new ResponderResult(new SharedSecret(new byte[] { 0xBB }), UsedOneTimeKey: false);

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
