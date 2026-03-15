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
        var invitation = new HandshakeInvitation(request.ToByteArray());

        var parsed = new ParsedInvitation(
            new RatchetIdentityKey(new byte[] { 0xAA }),
            new RatchetEphemeralKey(new byte[] { 0xBB }),
            "spk-1",
            "opk-1");

        var bridgeResult = new ResponderResult(new SharedSecret(new byte[] { 0xCC }), UsedOneTimeKey: true);

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
                It.Is<RatchetState>(s => s.RootKey.Value == bridgeResult.InitialRootKey.Value),
                It.IsAny<Plaintext>(),
                It.IsAny<AssociatedData>(),
                0,
                0))
            .Returns((
                new Ciphertext(new byte[] { 0x10, 0x11 }),
                new RatchetEphemeralKey(new byte[] { 0x20, 0x21 }),
                new RatchetState(new RootKey(bridgeResult.InitialRootKey.Value), null, 0, null, 0, 0, null, null, 1)));

        var crypto = new CryptoPrimitives(parserMock.Object, bridgeMock.Object, ratchetMock.Object);

        // ACT
        var response = crypto.CreateHandshakeResponse(invitation, new Mock<IKeyStore>().Object);

        // ASSERT
        response.Value.Should().NotBeEmpty();
        var msg = new SessionRatchetMessage(response.Value);
        var (_, counter, prevLen) = msg.GetHeader();
        var ct = msg.GetCiphertext();
        counter.Should().Be(0);
        prevLen.Should().Be(0);
        ct.Value.Should().Equal(new byte[] { 0x10, 0x11 });
    }

    [Test]
    public void CreateHandshakeResponse_WhenParserFails_Throws()
    {
        // ARRANGE
        var invitation = new HandshakeInvitation(new byte[] { 0xFF });
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
