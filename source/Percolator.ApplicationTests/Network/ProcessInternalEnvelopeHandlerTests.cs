using System.Net;
using MediatR;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Percolator.Application.Network;
using Percolator.Contracts;
using Percolator.Dht;
using Google.Protobuf.WellKnownTypes;
using Percolator.Application.Chat.MessageQueue;
using Percolator.Chat.GroupLedger;
using Percolator.Chat.Messaging.App;

namespace Percolator.ApplicationTests.Network
{
    public class ProcessInternalEnvelopeHandlerTests
    {
        private static ProcessInternalEnvelopeHandler CreateSut(
            Mock<IMediator> mediatorMock)
        {
            var logger = NullLogger<ProcessInternalEnvelopeHandler>.Instance;
            var dht = new Moq.Mock<Percolator.Dht.IDhtService>(MockBehavior.Loose);
            var mq = new Moq.Mock<IMessageQueueService>(MockBehavior.Loose);
            var profile = new Moq.Mock<Percolator.Network.IPeerRoutingProfileRepository>(MockBehavior.Loose);
            var pendingGroupInvitationRepo = new Moq.Mock<Percolator.Chat.IPendingGroupInvitationRepository>(MockBehavior.Loose);
            var groupMessageCryptoService = new Moq.Mock<Percolator.Cryptography.IGroupMessageCryptographyService>(MockBehavior.Loose);
            var messageWriter = new Moq.Mock<IChatMessageWriter>(MockBehavior.Loose);
            var profileOrchestrationService = new Moq.Mock<Percolator.Application.Chat.IProfileOrchestrationService>(MockBehavior.Loose);
            var groupInviteHandler = new Moq.Mock<Percolator.Application.Chat.IGroupInviteHandler>(MockBehavior.Loose);
            var peerIdentityQueries = new Moq.Mock<Percolator.Application.Chat.IPeerIdentityQueries>(MockBehavior.Loose);
            return new ProcessInternalEnvelopeHandler(logger, mediatorMock.Object, dht.Object, mq.Object, profile.Object, pendingGroupInvitationRepo.Object, groupMessageCryptoService.Object, messageWriter.Object, profileOrchestrationService.Object, groupInviteHandler.Object, peerIdentityQueries.Object);
        }

        [Test]
        public void EmojiAnnotation_throws_when_invalid_public_key_hash_length()
        {
            var mediator = new Mock<IMediator>(MockBehavior.Loose);
            var sut = CreateSut(mediator);

            var em = new EmojiAnnotation
            {
                MessageId = Google.Protobuf.ByteString.CopyFrom(Guid.NewGuid().ToByteArray()),
                Emoji = ":)",
                SentTimestampUtc = Timestamp.FromDateTime(DateTime.UtcNow),
                PublicIdentityId = Google.Protobuf.ByteString.CopyFrom(new byte[15]) // invalid
            };
            var env = new InternalEnvelope { ChatEnvelope = new ChatEnvelope { EmojiAnnotation = em } };
            var ctx = new SessionContext(Guid.NewGuid(), new Percolator.Identity.SelfId(1), null, null);

            Assert.ThrowsAsync<InvalidOperationException>(() => sut.Handle(new ProcessInternalEnvelopeCommand(env, ctx), CancellationToken.None));
        }

        [Test]
        public void EmojiAnnotation_throws_when_message_id_missing()
        {
            var mediator = new Mock<IMediator>(MockBehavior.Loose);
            var sut = CreateSut(mediator);

            var em = new EmojiAnnotation
            {
                // MessageId omitted
                Emoji = ":)",
                SentTimestampUtc = Timestamp.FromDateTime(DateTime.UtcNow)
            };
            var env = new InternalEnvelope { ChatEnvelope = new ChatEnvelope { EmojiAnnotation = em } };
            var ctx = new SessionContext(Guid.NewGuid(), new Percolator.Identity.SelfId(1), null, null);

            Assert.ThrowsAsync<InvalidOperationException>(() => sut.Handle(new ProcessInternalEnvelopeCommand(env, ctx), CancellationToken.None));
        }

        [Test]
        public void DeliveredReceipt_throws_when_invalid_public_key_hash_length()
        {
            var mediator = new Mock<IMediator>(MockBehavior.Loose);
            var sut = CreateSut(mediator);

            var dr = new DeliveredReceipt
            {
                MessageId = Google.Protobuf.ByteString.CopyFrom(Guid.NewGuid().ToByteArray()),
                SentTimestampUtc = Timestamp.FromDateTime(DateTime.UtcNow),
                PublicIdentityId = Google.Protobuf.ByteString.CopyFrom(new byte[15]) // invalid
            };
            var env = new InternalEnvelope { ChatEnvelope = new ChatEnvelope { DeliveredReceipt = dr } };
            var ctx = new SessionContext(Guid.NewGuid(), new Percolator.Identity.SelfId(1), null, null);

            Assert.ThrowsAsync<InvalidOperationException>(() => sut.Handle(new ProcessInternalEnvelopeCommand(env, ctx), CancellationToken.None));
        }

        [Test]
        public void DeliveredReceipt_throws_when_message_id_missing()
        {
            var mediator = new Mock<IMediator>(MockBehavior.Loose);
            var sut = CreateSut(mediator);

            var dr = new DeliveredReceipt
            {
                // MessageId omitted
                SentTimestampUtc = Timestamp.FromDateTime(DateTime.UtcNow)
            };
            var env = new InternalEnvelope { ChatEnvelope = new ChatEnvelope { DeliveredReceipt = dr } };
            var ctx = new SessionContext(Guid.NewGuid(), new Percolator.Identity.SelfId(1), null, null);

            Assert.ThrowsAsync<InvalidOperationException>(() => sut.Handle(new ProcessInternalEnvelopeCommand(env, ctx), CancellationToken.None));
        }

        [Test]
        public void TextMessage_throws_when_invalid_public_key_hash_length()
        {
            var mediator = new Mock<IMediator>(MockBehavior.Loose);
            var sut = CreateSut(mediator);

            var msg = new TextMessage
            {
                MessageId = Google.Protobuf.ByteString.CopyFrom(Guid.NewGuid().ToByteArray()),
                Content = "x",
                SentTimestampUtc = Timestamp.FromDateTime(DateTime.UtcNow),
                PublicIdentityId = Google.Protobuf.ByteString.CopyFrom(new byte[15]) // invalid length
            };
            var env = new InternalEnvelope { ChatEnvelope = new ChatEnvelope { TextMessage = msg } };
            var ctx = new SessionContext(Guid.NewGuid(), new Percolator.Identity.SelfId(1), null, null);

            Assert.ThrowsAsync<InvalidOperationException>(() => sut.Handle(new ProcessInternalEnvelopeCommand(env, ctx), CancellationToken.None));
        }

        [Test]
        public void TextMessage_throws_when_message_id_missing_or_invalid_length()
        {
            var mediator = new Mock<IMediator>(MockBehavior.Loose);
            var sut = CreateSut(mediator);

            var msg = new TextMessage
            {
                // MessageId intentionally omitted (null) to trigger validation
                Content = "x",
                SentTimestampUtc = Timestamp.FromDateTime(DateTime.UtcNow)
            };
            var env = new InternalEnvelope { ChatEnvelope = new ChatEnvelope { TextMessage = msg } };
            var ctx = new SessionContext(Guid.NewGuid(), new Percolator.Identity.SelfId(1), null, null);

            Assert.ThrowsAsync<InvalidOperationException>(() => sut.Handle(new ProcessInternalEnvelopeCommand(env, ctx), CancellationToken.None));
        }


        [Test]
        public async Task FindNodeRequest_zero_results_returns_empty_list()
        {
            var mediator = new Mock<IMediator>(MockBehavior.Loose);
            var dht = new Mock<Percolator.Dht.IDhtService>(MockBehavior.Strict);
            dht.Setup(s => s.GetClosestNodesAsync(It.IsAny<NodeId>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync(Array.Empty<Percolator.Dht.DhtNode>());

            var logger = NullLogger<ProcessInternalEnvelopeHandler>.Instance;
            var mq = new Moq.Mock<IMessageQueueService>(MockBehavior.Loose);
            var profile = new Moq.Mock<Percolator.Network.IPeerRoutingProfileRepository>(MockBehavior.Loose);
            var pendingGroupInvitationRepo = new Moq.Mock<Percolator.Chat.IPendingGroupInvitationRepository>(MockBehavior.Loose);
            var groupMessageCryptoService = new Moq.Mock<Percolator.Cryptography.IGroupMessageCryptographyService>(MockBehavior.Loose);
            var messageWriter = new Moq.Mock<IChatMessageWriter>(MockBehavior.Loose);
            var profileOrchestrationService = new Moq.Mock<Percolator.Application.Chat.IProfileOrchestrationService>(MockBehavior.Loose);
            var groupInviteHandler = new Moq.Mock<Percolator.Application.Chat.IGroupInviteHandler>(MockBehavior.Loose);
            var peerIdentityQueries = new Moq.Mock<Percolator.Application.Chat.IPeerIdentityQueries>(MockBehavior.Loose);
            var sut = new ProcessInternalEnvelopeHandler(logger, mediator.Object, dht.Object, mq.Object, profile.Object, pendingGroupInvitationRepo.Object, groupMessageCryptoService.Object, messageWriter.Object, profileOrchestrationService.Object, groupInviteHandler.Object, peerIdentityQueries.Object);

            var contractsReq = new Percolator.Contracts.FindNodeRequest
            {
                TargetPeerId = Google.Protobuf.ByteString.CopyFrom(new byte[32])
            };
            var env = new InternalEnvelope { DhtEnvelope = new DhtEnvelope { FindNodeRequest = contractsReq } };
            var ctx = new SessionContext(null, new Percolator.Identity.SelfId(1), null, new Percolator.Identity.DeviceId(1));

            var result = await sut.Handle(new ProcessInternalEnvelopeCommand(env, ctx), CancellationToken.None);
            Assert.That(result, Is.Not.Null);
            Assert.That(result!.DhtEnvelope.FindNodeResponse.CloserPeers.Count, Is.EqualTo(0));
            dht.VerifyAll();
        }

        [Test]
        public async Task FindNodeRequest_multiple_results_maps_all()
        {
            var mediator = new Mock<IMediator>(MockBehavior.Loose);
            var nodeId = NodeId.FromBytes(new byte[32]);
            var dns1 = new DnsEndPoint("10.0.0.1", 1234);
            var dns2 = new DnsEndPoint("10.0.0.2", 5678);
            var nodes = new[]
            {
                new Percolator.Dht.DhtNode(nodeId, dns1, DateTimeOffset.UtcNow),
                new Percolator.Dht.DhtNode(nodeId, dns2, DateTimeOffset.UtcNow)
            };
            var dht = new Mock<Percolator.Dht.IDhtService>(MockBehavior.Strict);
            dht.Setup(s => s.GetClosestNodesAsync(It.IsAny<NodeId>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync(nodes);

            var logger = NullLogger<ProcessInternalEnvelopeHandler>.Instance;
            var mq = new Moq.Mock<IMessageQueueService>(MockBehavior.Loose);
            var profile = new Moq.Mock<Percolator.Network.IPeerRoutingProfileRepository>(MockBehavior.Loose);
            var pendingGroupInvitationRepo = new Moq.Mock<Percolator.Chat.IPendingGroupInvitationRepository>(MockBehavior.Loose);
            var groupMessageCryptoService = new Moq.Mock<Percolator.Cryptography.IGroupMessageCryptographyService>(MockBehavior.Loose);
            var messageWriter = new Moq.Mock<IChatMessageWriter>(MockBehavior.Loose);
            var profileOrchestrationService = new Moq.Mock<Percolator.Application.Chat.IProfileOrchestrationService>(MockBehavior.Loose);
            var groupInviteHandler = new Moq.Mock<Percolator.Application.Chat.IGroupInviteHandler>(MockBehavior.Loose);
            var peerIdentityQueries = new Moq.Mock<Percolator.Application.Chat.IPeerIdentityQueries>(MockBehavior.Loose);
            var sut = new ProcessInternalEnvelopeHandler(logger, mediator.Object, dht.Object, mq.Object, profile.Object, pendingGroupInvitationRepo.Object, groupMessageCryptoService.Object, messageWriter.Object, profileOrchestrationService.Object, groupInviteHandler.Object, peerIdentityQueries.Object);
            var contractsReq = new Percolator.Contracts.FindNodeRequest
            {
                TargetPeerId = Google.Protobuf.ByteString.CopyFrom(new byte[32])
            };
            var env = new InternalEnvelope { DhtEnvelope = new DhtEnvelope { FindNodeRequest = contractsReq } };
            var ctx = new SessionContext(null, new Percolator.Identity.SelfId(1), null, new Percolator.Identity.DeviceId(1));

            var result = await sut.Handle(new ProcessInternalEnvelopeCommand(env, ctx), CancellationToken.None);
            Assert.That(result, Is.Not.Null);
            Assert.That(result!.DhtEnvelope.FindNodeResponse.CloserPeers.Count, Is.EqualTo(2));
            Assert.That(result.DhtEnvelope.FindNodeResponse.CloserPeers[0].Address, Is.EqualTo("10.0.0.1:1234"));
            Assert.That(result.DhtEnvelope.FindNodeResponse.CloserPeers[1].Address, Is.EqualTo("10.0.0.2:5678"));
            mediator.VerifyAll();
        }



        [Test]
        public async Task FindNodeRequest_returns_response_envelope()
        {
            var mediator = new Mock<IMediator>(MockBehavior.Loose);

            // Arrange DHT service response
            var nodeId = NodeId.FromBytes(new byte[] { 1,2,3,4, 5,6,7,8, 9,10,11,12, 13,14,15,16,
                                                  17,18,19,20, 21,22,23,24, 25,26,27,28, 29,30,31,32 });
            var dns = new DnsEndPoint("127.0.0.1", 3030);
            var dhtNode = new DhtNode(nodeId, dns, DateTimeOffset.UtcNow);
            var dht = new Mock<Percolator.Dht.IDhtService>(MockBehavior.Strict);
            dht.Setup(s => s.GetClosestNodesAsync(It.IsAny<NodeId>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync(new[] { dhtNode });

            var logger = NullLogger<ProcessInternalEnvelopeHandler>.Instance;
            var mq4 = new Moq.Mock<IMessageQueueService>(MockBehavior.Loose);
            var pendingGroupInvitationRepo = new Moq.Mock<Percolator.Chat.IPendingGroupInvitationRepository>(MockBehavior.Loose);
            var groupMessageCryptoService = new Moq.Mock<Percolator.Cryptography.IGroupMessageCryptographyService>(MockBehavior.Loose);
            var messageWriter = new Moq.Mock<IChatMessageWriter>(MockBehavior.Loose);
            var profileOrchestrationService = new Moq.Mock<Percolator.Application.Chat.IProfileOrchestrationService>(MockBehavior.Loose);
            var groupInviteHandler = new Moq.Mock<Percolator.Application.Chat.IGroupInviteHandler>(MockBehavior.Loose);
            var peerIdentityQueries = new Moq.Mock<Percolator.Application.Chat.IPeerIdentityQueries>(MockBehavior.Loose);
            var sut = new ProcessInternalEnvelopeHandler(logger, mediator.Object, dht.Object, mq4.Object, new Mock<Percolator.Network.IPeerRoutingProfileRepository>().Object, pendingGroupInvitationRepo.Object, groupMessageCryptoService.Object, messageWriter.Object, profileOrchestrationService.Object, groupInviteHandler.Object, peerIdentityQueries.Object);

            // Build InternalEnvelope with DHT FindNodeRequest
            var contractsReq = new Percolator.Contracts.FindNodeRequest
            {
                TargetPeerId = Google.Protobuf.ByteString.CopyFrom(nodeId.ToArray())
            };
            var env = new InternalEnvelope
            {
                DhtEnvelope = new DhtEnvelope { FindNodeRequest = contractsReq }
            };
            var ctx = new SessionContext(null, new Percolator.Identity.SelfId(1), null, new Percolator.Identity.DeviceId(1));

            // Act
            var result = await sut.Handle(new ProcessInternalEnvelopeCommand(env, ctx), CancellationToken.None);

            // Assert
            Assert.That(result, Is.Not.Null);
            Assert.That(result!.ApplicationPayloadCase, Is.EqualTo(InternalEnvelope.ApplicationPayloadOneofCase.DhtEnvelope));
            Assert.That(result.DhtEnvelope.FindNodeResponse, Is.Not.Null);
            Assert.That(result.DhtEnvelope.FindNodeResponse.CloserPeers.Count, Is.EqualTo(1));
            Assert.That(result.DhtEnvelope.FindNodeResponse.CloserPeers[0].Address, Is.EqualTo("127.0.0.1:3030"));

            dht.VerifyAll();
        }

        [Test]
        public async Task NonDht_returns_null()
        {
            var mediator = new Mock<IMediator>(MockBehavior.Strict);
            var sut = CreateSut(mediator);

            var env = new InternalEnvelope { ChatEnvelope = new ChatEnvelope { Version = 1 } };
            var ctx = new SessionContext(null, new Percolator.Identity.SelfId(1), null, new Percolator.Identity.DeviceId(1));

            var result = await sut.Handle(new ProcessInternalEnvelopeCommand(env, ctx), CancellationToken.None);
            Assert.That(result, Is.Null);
            mediator.VerifyNoOtherCalls();
        }

    }
}
