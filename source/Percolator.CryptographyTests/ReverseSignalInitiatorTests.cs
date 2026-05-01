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
        var localIk = RatchetIdentityKey.FromBytes(new byte[64]);
        var remoteIk = RatchetIdentityKey.FromBytes(new byte[64]);
        var spk = PreKey.FromBytes(new byte[64]);
        var sig = Signature.FromBytes(new byte[60]);
        var spkId = Guid.NewGuid();
        var opkId = Guid.NewGuid();
        var otk = OneTimeKey.FromBytes(new byte[64]);
        var bundle = new PreKeyBundle(remoteIk, spkId, spk, sig, opkId, otk, null);

        var idPriv = PrivatePreKey.FromBytes(new byte[100]);
        var initResult = new InitiatorResult(
            SharedSecret.FromBytes(new byte[32]),
            RatchetEphemeralKey.FromBytes(new byte[64]),
            PrivatePreKey.FromBytes(new byte[100]),
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
        parsed.InitiatorIdentityKey.ToArray().Should().Equal(localIk.ToArray());
        parsed.InitiatorEphemeralKey.ToArray().Should().Equal(initResult.InitiatorEphemeralPublicKey.ToArray());
        parsed.SignedPreKeyId.Should().Be(spkId.ToString());
        parsed.OneTimePreKeyId.Should().Be(opkId.ToString());
    }

    [Test]
    public void CreateInitialMessage_ProducesRatchetMessage_FromInitiatorResult()
    {
        // ARRANGE
        var initResult = new InitiatorResult(
            SharedSecret.FromBytes(new byte[32]),
            RatchetEphemeralKey.FromBytes(new byte[64]),
            PrivatePreKey.FromBytes(new byte[100]),
            UsedOneTimeKey: false);

        var deriver = new Mock<IX3dhDeriver>(MockBehavior.Loose);
        var keystore = new Mock<IKeyStore>(MockBehavior.Loose);
        var parser = new HandshakeInvitationParser();

        var ratchet = new Mock<IRatchetEngine>(MockBehavior.Strict);
        ratchet
            .Setup(r => r.Encrypt(
                It.IsAny<RatchetState>(),
                It.IsAny<Plaintext>(),
                It.IsAny<AssociatedData>(),
                It.IsAny<ulong>(),
                It.IsAny<ulong>()))
            .Returns((
                Ciphertext.FromBytes(new byte[] { 0x10, 0x11 }),
                RatchetEphemeralKey.FromBytes(new byte[64]),
                new RatchetState(RootKey.FromBytes(initResult.InitialRootKey.ToArray()), null, 0, null, 0, 0, null, null, 1)));

        var initiator = new ReverseSignalInitiator(deriver.Object, keystore.Object, parser, ratchet.Object);

        // ACT
        var msg = initiator.CreateInitialMessage(initResult);

        // ASSERT
        msg.ToArray().Should().NotBeEmpty();
        var parsedHeader = msg.GetHeader();
        var parsedCipher = msg.GetCiphertext();
        parsedHeader.Counter.Should().Be(0);
        parsedHeader.PreviousChainLength.Should().Be(0);
        parsedCipher.ToArray().Should().Equal(new byte[] { 0x10, 0x11 });
    }
}
