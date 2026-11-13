namespace Percolator.Infrastructure.Tests.Protos;

using FluentAssertions;
using Google.Protobuf;
using NUnit.Framework;
using Percolator.Infrastructure.Protos;
using Percolator.Infrastructure2.Protos;

[TestFixture]
public sealed class CoreProtoCompilationTests
{
    [Test]
    public void AllProtos_GenerateValidClrTypes()
    {
        // 1. Unauthenticated Delivery
        var sealedEnvelope = new SealedEnvelopeProto
        {
            RecipientRoutingToken = ByteString.CopyFrom(new byte[16]),
            DeliveryToken = ByteString.CopyFrom(new byte[32]),
            EncryptedPackage = ByteString.CopyFrom([1, 2, 3])
        };
        sealedEnvelope.ToByteArray().Should().NotBeEmpty();

        var preKeyBundleReq = new FetchPreKeyBundleRequest
        {
            RecipientRoutingToken = ByteString.CopyFrom(new byte[16]),
            DeliveryToken = ByteString.CopyFrom(new byte[32])
        };
        preKeyBundleReq.ToByteArray().Should().NotBeEmpty();

        var preKeyBundleResp = new FetchPreKeyBundleResponse
        {
            IdentityKey = ByteString.CopyFrom(new byte[32]),
            SignedPrekey = ByteString.CopyFrom(new byte[32]),
            PrekeySignature = ByteString.CopyFrom(new byte[64]),
            SignedPrekeyId = 1,
            OneTimePrekey = ByteString.CopyFrom(new byte[32]),
            OneTimePrekeyId = 100
        };
        preKeyBundleResp.ToByteArray().Should().NotBeEmpty();

        // 2. Authenticated Relay
        var registerMailboxReq = new RegisterMailboxRequest
        {
            RoutingToken = ByteString.CopyFrom(new byte[16]),
            AuthorizedDeliveryToken = ByteString.CopyFrom(new byte[32]),
            DrainToken = ByteString.CopyFrom(new byte[32])
        };
        registerMailboxReq.ToByteArray().Should().NotBeEmpty();

        var publishPreKeyReq = new PublishPreKeyBundleRequest
        {
            PayloadBytes = ByteString.CopyFrom([0xAA, 0xBB]),
            Signature = ByteString.CopyFrom(new byte[64]),
            DrainToken = ByteString.CopyFrom(new byte[32])
        };
        publishPreKeyReq.ToByteArray().Should().NotBeEmpty();
        publishPreKeyReq.DrainToken.Length.Should().Be(32);

        // 3. Anonymous Group
        var groupMsgReq = new DispatchGroupMessageRequest
        {
            ConversationId = ByteString.CopyFrom(new byte[16]),
            Epoch = 1,
            ZkPresentationProof = ByteString.CopyFrom(new byte[72]),
            SenderKeyIteration = 5,
            Ciphertext = ByteString.CopyFrom([1, 2, 3]),
            AuthorSignature = ByteString.CopyFrom(new byte[64])
        };
        groupMsgReq.ToByteArray().Should().NotBeEmpty();

        // 4. Session
        var directSession = new DirectSessionPackageProto
        {
            Header = new RatchetHeaderProto
            {
                RatchetPublicKey = ByteString.CopyFrom(new byte[32]),
                Counter = 10,
                PreviousChainLength = 2
            },
            Nonce = ByteString.CopyFrom(new byte[12]),
            Ciphertext = ByteString.CopyFrom([4, 5, 6])
        };
        directSession.ToByteArray().Should().NotBeEmpty();

        var groupSession = new GroupSessionPackageProto
        {
            ConversationId = ByteString.CopyFrom(new byte[16]),
            Iteration = 1,
            Ciphertext = ByteString.CopyFrom([7, 8, 9]),
            AuthorSignature = ByteString.CopyFrom(new byte[64])
        };
        groupSession.ToByteArray().Should().NotBeEmpty();

        var handshake = new HandshakeInvitationPackageProto
        {
            InitiatorIdentityKey = ByteString.CopyFrom(new byte[32]),
            InitiatorEphemeralKey = ByteString.CopyFrom(new byte[32]),
            SignedPrekeyId = 2,
            OnetimePrekeyId = 42,
            EncryptedPayload = ByteString.CopyFrom([10, 11, 12])
        };
        handshake.ToByteArray().Should().NotBeEmpty();

        // 5. Profile
        var profile = new ProfileMetadataPayloadProto
        {
            DisplayName = "Test User",
            Revision = 1
        };
        profile.ToByteArray().Should().NotBeEmpty();
    }
}
