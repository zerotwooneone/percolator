using FluentAssertions;
using Moq;
using Percolator.Cryptography;

namespace Percolator.CryptographyTests;

[TestFixture]
public class ReverseSignalInitiatorTests
{
    [Test]
    public void CreateInvitation_UsesBundleIds_And_RoundTripsViaParser()
    {
        // ARRANGE
        var localIk = new RatchetIdentityKey(new byte[] { 0x01 });
        var remoteIk = new RatchetIdentityKey(new byte[] { 0x02 });
        var spk = new PreKey(new byte[] { 0x03 });
        var sig = new Signature(new byte[] { 0x04 });
        var spkId = Guid.NewGuid();
        var opkId = Guid.NewGuid();
        var otk = new OneTimeKey(new byte[] { 0x05 });
        var bundle = new PreKeyBundle(remoteIk, spkId, spk, sig, opkId, otk, null);

        var idPriv = new PrivatePreKey(new byte[] { 0x10 });
        var initResult = new InitiatorResult(
            new SharedSecret(new byte[] { 0xAA }),
            new RatchetEphemeralKey(new byte[] { 0xBB }),
            new PrivatePreKey(new byte[] { 0xCC }),
            UsedOneTimeKey: true);

        var deriver = new Mock<IX3dhDeriver>(MockBehavior.Strict);
        deriver
            .Setup(d => d.DeriveInitiator(remoteIk, spk, otk, idPriv))
            .Returns(initResult);

        var keystore = new Mock<IKeyStore>(MockBehavior.Strict);
        keystore.Setup(k => k.GetIdentityPrivateKey()).Returns(idPriv);

        var parser = new HandshakeInvitationParser();
        var ratchet = new Mock<IRatchetEngine>(MockBehavior.Loose);
        var initiator = new ReverseSignalInitiator(deriver.Object, keystore.Object, parser, ratchet.Object);

        // ACT
        var (invitation, result) = initiator.CreateInvitation(localIk, bundle);
        var parsed = parser.Parse(invitation);

        // ASSERT
        result.Should().Be(initResult);
        parsed.InitiatorIdentityKey.Value.Should().Equal(localIk.Value);
        parsed.InitiatorEphemeralKey.Value.Should().Equal(initResult.InitiatorEphemeralPublicKey.Value);
        parsed.SignedPreKeyId.Should().Be(spkId.ToString());
        parsed.OneTimePreKeyId.Should().Be(opkId.ToString());
    }

    [Test]
    public void CreateInitialMessage_ProducesRatchetMessage_FromInitiatorResult()
    {
        // ARRANGE
        var initResult = new InitiatorResult(
            new SharedSecret(new byte[] { 0xAA }),
            new RatchetEphemeralKey(new byte[] { 0xBB }),
            new PrivatePreKey(new byte[] { 0xCC }),
            UsedOneTimeKey: false);

        var deriver = new Mock<IX3dhDeriver>(MockBehavior.Loose);
        var keystore = new Mock<IKeyStore>(MockBehavior.Loose);
        var parser = new HandshakeInvitationParser();

        var ratchet = new Mock<IRatchetEngine>(MockBehavior.Strict);
        ratchet
            .Setup(r => r.Encrypt(
                It.Is<RatchetState>(s => s.RootKey.Value == initResult.InitialRootKey.Value),
                It.IsAny<Plaintext>(),
                It.IsAny<AssociatedData>(),
                0,
                0))
            .Returns((
                new Ciphertext(new byte[] { 0x10, 0x11 }),
                new RatchetEphemeralKey(new byte[] { 0x20, 0x21 }),
                new RatchetState(new RootKey(initResult.InitialRootKey.Value), null, 0, null, 0, 0, null, null, 1)));

        var initiator = new ReverseSignalInitiator(deriver.Object, keystore.Object, parser, ratchet.Object);

        // ACT
        var msg = initiator.CreateInitialMessage(initResult);

        // ASSERT
        msg.Value.Should().NotBeEmpty();
        var parsedHeader = msg.GetHeader();
        var parsedCipher = msg.GetCiphertext();
        parsedHeader.Counter.Should().Be(0);
        parsedHeader.PreviousChainLength.Should().Be(0);
        parsedCipher.Value.Should().Equal(new byte[] { 0x10, 0x11 });
    }
}
