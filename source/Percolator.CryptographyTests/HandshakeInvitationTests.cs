using FluentAssertions;
using Google.Protobuf;
using Percolator.Contracts;
using Percolator.Cryptography;

namespace Percolator.CryptographyTests;

[TestFixture]
public class HandshakeInvitationTests
{
    [Test]
    public void Parse_GivenValidEstablishSessionRequest_PopulatesAllFields()
    {
        // ARRANGE
        var request = new EstablishSessionRequest
        {
            Version = 1,
            IdentitySigningKey = ByteString.CopyFrom(new byte[64]),
            EphemeralKey = ByteString.CopyFrom(new byte[64]),
            PrekeyId = ByteString.CopyFromUtf8("spk-1"),
            OnetimePrekeyId = ByteString.CopyFromUtf8("opk-1")
        };
        var bytes = request.ToByteArray();
        var invitation = HandshakeInvitation.FromBytes(bytes);

        // ACT
        var parsed = HandshakeInvitationParser.Internal_Parse(invitation);

        // ASSERT
        parsed.InitiatorIdentityKey.ToArray().Should().Equal(request.IdentitySigningKey.ToByteArray());
        parsed.InitiatorEphemeralKey.ToArray().Should().Equal(request.EphemeralKey.ToByteArray());
        parsed.SignedPreKeyId.Should().Be("spk-1");
        parsed.OneTimePreKeyId.Should().Be("opk-1");
    }

    [Test]
    public void Parse_GivenMissingRequiredFields_Throws()
    {
        // ARRANGE: no identity key, no ephemeral key, no prekey id
        var request = new EstablishSessionRequest { Version = 1 };
        var bytes = request.ToByteArray();
        var invitation = HandshakeInvitation.FromBytes(bytes);

        // ACT
        Action act = () => HandshakeInvitationParser.Internal_Parse(invitation);

        // ASSERT
        act.Should().Throw<InvalidOperationException>();
    }

    [Test]
    public void Build_And_Parse_RoundTrips_AllFields()
    {
        // ARRANGE
        var ik = RatchetIdentityKey.FromBytes(new byte[64]);
        var ek = RatchetEphemeralKey.FromBytes(new byte[64]);
        const string spkId = "spk-xyz";
        const string opkId = "opk-abc";

        // ACT
        var invitation = HandshakeInvitationBuilder.Build(ik, ek, spkId, opkId);
        var parsed = HandshakeInvitationParser.Internal_Parse(invitation);

        // ASSERT
        parsed.InitiatorIdentityKey.ToArray().Should().Equal(ik.ToArray());
        parsed.InitiatorEphemeralKey.ToArray().Should().Equal(ek.ToArray());
        parsed.SignedPreKeyId.Should().Be(spkId);
        parsed.OneTimePreKeyId.Should().Be(opkId);
    }
}
