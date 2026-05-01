using FluentAssertions;
using Google.Protobuf;
using Moq;
using Percolator.Contracts;
using Percolator.Cryptography;

namespace Percolator.CryptographyTests;

[TestFixture]
public class CryptoPrimitivesTests
{
    [Test]
    public void CreateHandshakeResponse_WithValidInvitation_ProducesValidRatchetMessage()
    {
        // ARRANGE
        var request = new EstablishSessionRequest
        {
            Version = 1,
            IdentitySigningKey = ByteString.CopyFrom(new byte[] { 0x01 }),
            EphemeralKey = ByteString.CopyFrom(new byte[] { 0x02 }),
            PrekeyId = ByteString.CopyFromUtf8("spk-1"),
            OnetimePrekeyId = ByteString.CopyFromUtf8("opk-1")
        };
        var invitation = HandshakeInvitation.FromBytes(request.ToByteArray());

        var parsed = new ParsedInvitation(
            RatchetIdentityKey.FromBytes(new byte[64]),
            RatchetEphemeralKey.FromBytes(new byte[64]),
            "spk-1",
            "opk-1");

        var bridgeResult = new ResponderResult(SharedSecret.FromBytes(new byte[32]), UsedOneTimeKey: true);

        var parserMock = new Mock<IHandshakeInvitationParser>(MockBehavior.Strict);
        parserMock
            .Setup(p => p.Parse(invitation))
            .Returns(parsed);

        var bridgeMock = new Mock<IX3dhResponderBridge>(MockBehavior.Strict);
        bridgeMock
            .Setup(b => b.DeriveResponder(parsed))
            .Returns(bridgeResult);

        var ratchetMock = new Mock<IRatchetEngine>(MockBehavior.Strict);
        ratchetMock
            .Setup(r => r.Encrypt(
                It.IsAny<RatchetState>(),
                It.IsAny<Plaintext>(),
                It.IsAny<AssociatedData>(),
                It.IsAny<ulong>(),
                It.IsAny<ulong>()))
            .Returns((
                Ciphertext.FromBytes(new byte[] { 0x10, 0x11 }),
                RatchetEphemeralKey.FromBytes(new byte[64]),
                new RatchetState(RootKey.FromBytes(bridgeResult.InitialRootKey.ToArray()), null, 0, null, 0, 0, null, null, 1)));

        var crypto = new CryptoPrimitives(parserMock.Object, bridgeMock.Object, ratchetMock.Object);

        // ACT
        var response = crypto.CreateHandshakeResponse(invitation, new Mock<IKeyStore>().Object);

        // ASSERT
        response.ToArray().Should().NotBeEmpty();
        var msg = SessionRatchetMessage.FromBytes(response.ToArray());
        var (_, counter, prevLen) = msg.GetHeader();
        var ct = msg.GetCiphertext();
        counter.Should().Be(0);
        prevLen.Should().Be(0);
        ct.ToArray().Should().Equal(new byte[] { 0x10, 0x11 });
    }

    [Test]
    public void CreateHandshakeResponse_WhenParserFails_Throws()
    {
        // ARRANGE
        var invitation = HandshakeInvitation.FromBytes(new byte[] { 0xFF });
        var parserMock = new Mock<IHandshakeInvitationParser>(MockBehavior.Strict);
        parserMock
            .Setup(p => p.Parse(invitation))
            .Throws(new InvalidOperationException("bad invitation"));

        var bridgeMock = new Mock<IX3dhResponderBridge>(MockBehavior.Loose);
        var ratchetMock = new Mock<IRatchetEngine>(MockBehavior.Loose);

        var crypto = new CryptoPrimitives(parserMock.Object, bridgeMock.Object, ratchetMock.Object);

        // ACT
        Action act = () => crypto.CreateHandshakeResponse(invitation, new Mock<IKeyStore>().Object);

        // ASSERT
        act.Should().Throw<InvalidOperationException>();
    }
}
