using System.Net;
using MediatR;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Percolator.Application.Network;
using Percolator.Contracts;
using Percolator.Dht;
using Percolator.Chat.App.Commands;
using Google.Protobuf.WellKnownTypes;

namespace Percolator.ApplicationTests.Network
{
    public class ProcessInternalEnvelopeHandlerTests
    {
        private static ProcessInternalEnvelopeHandler CreateSut(
            Mock<IMediator> mediatorMock,
            Mock<Percolator.Chat.App.IPkhPeerResolver>? pkhPeerResolverMock = null)
        {
            var logger = NullLogger<ProcessInternalEnvelopeHandler>.Instance;
            var adminOps = new Moq.Mock<Percolator.Chat.App.IAdminOperations>(MockBehavior.Loose);
            var dht = new Moq.Mock<Percolator.Dht.IDhtService>(MockBehavior.Loose);
            var mq = new Moq.Mock<Percolator.MessageQueue.Abstractions.IMessageQueueService>(MockBehavior.Loose);
            var profile = new Moq.Mock<Percolator.Network.IPeerRoutingProfileRepository>(MockBehavior.Loose);
            pkhPeerResolverMock ??= new Moq.Mock<Percolator.Chat.App.IPkhPeerResolver>(MockBehavior.Loose);
            return new ProcessInternalEnvelopeHandler(logger, mediatorMock.Object, adminOps.Object, dht.Object, mq.Object, profile.Object, pkhPeerResolverMock.Object);
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
                PublicKeyHash = Google.Protobuf.ByteString.CopyFrom(new byte[31]) // invalid
            };
            var env = new InternalEnvelope { ChatEnvelope = new ChatEnvelope { EmojiAnnotation = em } };
            var ctx = new SessionContext(Guid.NewGuid(), new Percolator.Identity.SelfId(1), null);

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
            var ctx = new SessionContext(Guid.NewGuid(), new Percolator.Identity.SelfId(1), null);

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
                PublicKeyHash = Google.Protobuf.ByteString.CopyFrom(new byte[31]) // invalid
            };
            var env = new InternalEnvelope { ChatEnvelope = new ChatEnvelope { DeliveredReceipt = dr } };
            var ctx = new SessionContext(Guid.NewGuid(), new Percolator.Identity.SelfId(1), null);

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
            var ctx = new SessionContext(Guid.NewGuid(), new Percolator.Identity.SelfId(1), null);

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
                PublicKeyHash = Google.Protobuf.ByteString.CopyFrom(new byte[31]) // invalid length
            };
            var env = new InternalEnvelope { ChatEnvelope = new ChatEnvelope { TextMessage = msg } };
            var ctx = new SessionContext(Guid.NewGuid(), new Percolator.Identity.SelfId(1), null);

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
            var ctx = new SessionContext(Guid.NewGuid(), new Percolator.Identity.SelfId(1), null);

            Assert.ThrowsAsync<InvalidOperationException>(() => sut.Handle(new ProcessInternalEnvelopeCommand(env, ctx), CancellationToken.None));
        }

        [Test]
        public void TextMessage_throws_when_both_group_and_pkh_set()
        {
            var mediator = new Mock<IMediator>(MockBehavior.Loose);
            var sut = CreateSut(mediator);

            var msg = new TextMessage
            {
                MessageId = Google.Protobuf.ByteString.CopyFrom(Guid.NewGuid().ToByteArray()),
                Content = "x",
                SentTimestampUtc = Timestamp.FromDateTime(DateTime.UtcNow),
                GroupConversationGuid = Google.Protobuf.ByteString.CopyFrom(Guid.NewGuid().ToByteArray()),
                PublicKeyHash = Google.Protobuf.ByteString.CopyFrom(new byte[32])
            };
            var env = new InternalEnvelope { ChatEnvelope = new ChatEnvelope { TextMessage = msg } };
            var ctx = new SessionContext(Guid.NewGuid(), new Percolator.Identity.SelfId(1), null);

            Assert.ThrowsAsync<InvalidOperationException>(() => sut.Handle(new ProcessInternalEnvelopeCommand(env, ctx), CancellationToken.None));
        }

        [Test]
        public void ReadReceipt_throws_when_both_group_and_pkh_set()
        {
            var mediator = new Mock<IMediator>(MockBehavior.Loose);
            var sut = CreateSut(mediator);

            var rr = new ReadReceipt
            {
                MessageId = Google.Protobuf.ByteString.CopyFrom(Guid.NewGuid().ToByteArray()),
                SentTimestampUtc = Timestamp.FromDateTime(DateTime.UtcNow),
                GroupConversationGuid = Google.Protobuf.ByteString.CopyFrom(Guid.NewGuid().ToByteArray()),
                PublicKeyHash = Google.Protobuf.ByteString.CopyFrom(new byte[32])
            };
            var env = new InternalEnvelope { ChatEnvelope = new ChatEnvelope { ReadReceipt = rr } };
            var ctx = new SessionContext(Guid.NewGuid(), new Percolator.Identity.SelfId(1), null);

            Assert.ThrowsAsync<InvalidOperationException>(() => sut.Handle(new ProcessInternalEnvelopeCommand(env, ctx), CancellationToken.None));
        }

        [Test]
        public void EmojiAnnotation_throws_when_both_group_and_pkh_set()
        {
            var mediator = new Mock<IMediator>(MockBehavior.Loose);
            var sut = CreateSut(mediator);

            var em = new EmojiAnnotation
            {
                MessageId = Google.Protobuf.ByteString.CopyFrom(Guid.NewGuid().ToByteArray()),
                Emoji = ":)",
                SentTimestampUtc = Timestamp.FromDateTime(DateTime.UtcNow),
                GroupConversationGuid = Google.Protobuf.ByteString.CopyFrom(Guid.NewGuid().ToByteArray()),
                PublicKeyHash = Google.Protobuf.ByteString.CopyFrom(new byte[32])
            };
            var env = new InternalEnvelope { ChatEnvelope = new ChatEnvelope { EmojiAnnotation = em } };
            var ctx = new SessionContext(Guid.NewGuid(), new Percolator.Identity.SelfId(1), null);

            Assert.ThrowsAsync<InvalidOperationException>(() => sut.Handle(new ProcessInternalEnvelopeCommand(env, ctx), CancellationToken.None));
        }

        [Test]
        public void DeliveredReceipt_throws_when_both_group_and_pkh_set()
        {
            var mediator = new Mock<IMediator>(MockBehavior.Loose);
            var sut = CreateSut(mediator);

            var dr = new DeliveredReceipt
            {
                MessageId = Google.Protobuf.ByteString.CopyFrom(Guid.NewGuid().ToByteArray()),
                SentTimestampUtc = Timestamp.FromDateTime(DateTime.UtcNow),
                GroupConversationGuid = Google.Protobuf.ByteString.CopyFrom(Guid.NewGuid().ToByteArray()),
                PublicKeyHash = Google.Protobuf.ByteString.CopyFrom(new byte[32])
            };
            var env = new InternalEnvelope { ChatEnvelope = new ChatEnvelope { DeliveredReceipt = dr } };
            var ctx = new SessionContext(Guid.NewGuid(), new Percolator.Identity.SelfId(1), null);

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
            var adminOps = new Moq.Mock<Percolator.Chat.App.IAdminOperations>(MockBehavior.Loose);
            var mq = new Moq.Mock<Percolator.MessageQueue.Abstractions.IMessageQueueService>(MockBehavior.Loose);
            var profile = new Moq.Mock<Percolator.Network.IPeerRoutingProfileRepository>(MockBehavior.Loose);
            var pkhPeerResolver = new Moq.Mock<Percolator.Chat.App.IPkhPeerResolver>(MockBehavior.Loose);
            var sut = new ProcessInternalEnvelopeHandler(logger, mediator.Object, adminOps.Object, dht.Object, mq.Object, profile.Object, pkhPeerResolver.Object);

            var contractsReq = new Percolator.Contracts.FindNodeRequest
            {
                TargetPeerId = Google.Protobuf.ByteString.CopyFrom(new byte[32])
            };
            var env = new InternalEnvelope { DhtEnvelope = new DhtEnvelope { FindNodeRequest = contractsReq } };
            var ctx = new SessionContext(null, new Percolator.Identity.SelfId(1), null);

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
            var adminOps = new Moq.Mock<Percolator.Chat.App.IAdminOperations>(MockBehavior.Loose);
            var mq = new Moq.Mock<Percolator.MessageQueue.Abstractions.IMessageQueueService>(MockBehavior.Loose);
            var profile = new Moq.Mock<Percolator.Network.IPeerRoutingProfileRepository>(MockBehavior.Loose);
            var pkhPeerResolver = new Moq.Mock<Percolator.Chat.App.IPkhPeerResolver>(MockBehavior.Loose);
            var sut = new ProcessInternalEnvelopeHandler(logger, mediator.Object, adminOps.Object, dht.Object, mq.Object, profile.Object, pkhPeerResolver.Object);
            var contractsReq = new Percolator.Contracts.FindNodeRequest
            {
                TargetPeerId = Google.Protobuf.ByteString.CopyFrom(new byte[32])
            };
            var env = new InternalEnvelope { DhtEnvelope = new DhtEnvelope { FindNodeRequest = contractsReq } };
            var ctx = new SessionContext(null, new Percolator.Identity.SelfId(1), null);

            var result = await sut.Handle(new ProcessInternalEnvelopeCommand(env, ctx), CancellationToken.None);
            Assert.That(result, Is.Not.Null);
            Assert.That(result!.DhtEnvelope.FindNodeResponse.CloserPeers.Count, Is.EqualTo(2));
            Assert.That(result.DhtEnvelope.FindNodeResponse.CloserPeers[0].Address, Is.EqualTo("10.0.0.1:1234"));
            Assert.That(result.DhtEnvelope.FindNodeResponse.CloserPeers[1].Address, Is.EqualTo("10.0.0.2:5678"));
            mediator.VerifyAll();
        }

        [Test]
        public async Task Chat_ReadReceipt_is_dispatched_and_returns_null()
        {
            var mediator = new Mock<IMediator>(MockBehavior.Strict);
            mediator
                .Setup(m => m.Send(It.IsAny<ReceiveReadReceiptCommand>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            var authorSpki = new byte[] { 1, 2, 3 };
            var authorPkh = System.Security.Cryptography.SHA256.HashData(authorSpki);
            var resolvedParticipantId = new Percolator.Chat.ValueObjects.ParticipantId(Guid.NewGuid());
            var pkhPeerResolver = new Mock<Percolator.Chat.App.IPkhPeerResolver>(MockBehavior.Strict);
            pkhPeerResolver
                .Setup(r => r.GetParticipantIdByPkhAsync(
                    Percolator.Chat.ValueObjects.Pkh.FromBytes(authorPkh),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(resolvedParticipantId);

            var sut = CreateSut(mediator, pkhPeerResolver);

            var messageId = Guid.NewGuid();
            var groupId = Guid.NewGuid();
            var rr = new ReadReceipt
            {
                MessageId = Google.Protobuf.ByteString.CopyFrom(messageId.ToByteArray()),
                AuthorIdentityKey = Google.Protobuf.ByteString.CopyFrom(authorSpki),
                SentTimestampUtc = Timestamp.FromDateTime(DateTime.UtcNow)
            };
            rr.GroupConversationGuid = Google.Protobuf.ByteString.CopyFrom(groupId.ToByteArray());

            var env = new InternalEnvelope { ChatEnvelope = new ChatEnvelope { ReadReceipt = rr } };
            var ctx = new SessionContext(null, new Percolator.Identity.SelfId(1), null);

            var result = await sut.Handle(new ProcessInternalEnvelopeCommand(env, ctx), CancellationToken.None);
            Assert.That(result, Is.Null);
            mediator.VerifyAll();
        }

        [Test]
        public async Task Chat_SignedAdminOperation_is_applied_locally_and_returns_null()
        {
            var mediator = new Mock<IMediator>(MockBehavior.Loose);
            var logger = NullLogger<ProcessInternalEnvelopeHandler>.Instance;
            var adminOps = new Mock<Percolator.Chat.App.IAdminOperations>(MockBehavior.Loose);
            var mq2 = new Moq.Mock<Percolator.MessageQueue.Abstractions.IMessageQueueService>(MockBehavior.Loose);
            var pkhPeerResolver = new Moq.Mock<Percolator.Chat.App.IPkhPeerResolver>(MockBehavior.Loose);
            var sut = new ProcessInternalEnvelopeHandler(logger, mediator.Object, adminOps.Object, new Mock<Percolator.Dht.IDhtService>().Object, mq2.Object, new Mock<Percolator.Network.IPeerRoutingProfileRepository>().Object, pkhPeerResolver.Object);

            var groupId = Guid.NewGuid();
            var opId = Guid.NewGuid();
            var payload = new AdminOperationPayload
            {
                GroupConversationGuid = Google.Protobuf.ByteString.CopyFrom(groupId.ToByteArray()),
                OpId = Google.Protobuf.ByteString.CopyFrom(opId.ToByteArray()),
                SentTimestampUtc = Timestamp.FromDateTime(DateTime.UtcNow)
            };
            // Use GrantAdmin variant with a dummy grantee key
            payload.GrantAdmin = new GrantAdmin
            {
                GranteePublicKey = Google.Protobuf.ByteString.CopyFrom(new byte[] { 1, 2, 3 })
            };

            var sao = new SignedAdminOperation
            {
                Payload = payload,
                Signature = Google.Protobuf.ByteString.CopyFrom(new byte[] { 9, 9, 9 })
            };

            var env = new InternalEnvelope { ChatEnvelope = new ChatEnvelope { SignedAdminOperation = sao } };
            var ctx = new SessionContext(null, new Percolator.Identity.SelfId(1), null);

            var result = await sut.Handle(new ProcessInternalEnvelopeCommand(env, ctx), CancellationToken.None);
            Assert.That(result, Is.Null);
            mediator.VerifyAll();
        }

        [Test]
        public async Task Chat_AdminCommitOperation_is_dispatched_and_returns_null()
        {
            var mediator = new Mock<IMediator>(MockBehavior.Strict);
            mediator
                .Setup(m => m.Send(It.IsAny<ReceiveAdminCommitCommand>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            var sut = CreateSut(mediator);

            var groupId = Guid.NewGuid();
            var opId = Guid.NewGuid();
            var aco = new SignedAdminCommitOperation
            {
                GroupConversationGuid = Google.Protobuf.ByteString.CopyFrom(groupId.ToByteArray()),
                OpId = Google.Protobuf.ByteString.CopyFrom(opId.ToByteArray()),
                CommittedKeyVersion = 2,
                AdminSequenceNumber = 1,
                Signature = Google.Protobuf.ByteString.CopyFrom(new byte[] { 7, 7, 7 }),
                SentTimestampUtc = Timestamp.FromDateTime(DateTime.UtcNow)
            };

            var env = new InternalEnvelope { ChatEnvelope = new ChatEnvelope { AdminCommitOperation = aco } };
            var ctx = new SessionContext(null, new Percolator.Identity.SelfId(1), null);

            var result = await sut.Handle(new ProcessInternalEnvelopeCommand(env, ctx), CancellationToken.None);
            Assert.That(result, Is.Null);
            mediator.VerifyAll();
        }

        [Test]
        public async Task Chat_KeyAdoptionConfirmation_is_dispatched_and_returns_null()
        {
            var mediator = new Mock<IMediator>(MockBehavior.Strict);
            mediator
                .Setup(m => m.Send(It.IsAny<ReceiveKeyAdoptionConfirmationCommand>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            var sut = CreateSut(mediator);

            var groupId = Guid.NewGuid();
            var kac = new SignedKeyAdoptionConfirmation
            {
                GroupConversationGuid = Google.Protobuf.ByteString.CopyFrom(groupId.ToByteArray()),
                KeyVersion = 3,
                AdopterIdentityKey = Google.Protobuf.ByteString.CopyFrom(new byte[] { 5, 5, 5 }),
                Signature = Google.Protobuf.ByteString.CopyFrom(new byte[] { 6, 6, 6 }),
                SentTimestampUtc = Timestamp.FromDateTime(DateTime.UtcNow)
            };

            var env = new InternalEnvelope { ChatEnvelope = new ChatEnvelope { KeyAdoptionConfirmation = kac } };
            var ctx = new SessionContext(null, new Percolator.Identity.SelfId(1), null);

            var result = await sut.Handle(new ProcessInternalEnvelopeCommand(env, ctx), CancellationToken.None);
            Assert.That(result, Is.Null);
            mediator.VerifyAll();
        }

        [Test]
        public async Task Chat_KeyDistribution_is_dispatched_and_returns_null()
        {
            var mediator = new Mock<IMediator>(MockBehavior.Strict);
            mediator
                .Setup(m => m.Send(It.IsAny<ReceiveKeyDistributionCommand>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            var sut = CreateSut(mediator);

            var groupId = Guid.NewGuid();
            var kd = new KeyDistributionPayload
            {
                GroupConversationGuid = Google.Protobuf.ByteString.CopyFrom(groupId.ToByteArray()),
                KeyVersion = 4,
                EncryptedGroupKeyForRecipient = Google.Protobuf.ByteString.CopyFrom(new byte[] { 11, 22, 33 })
            };

            var env = new InternalEnvelope { ChatEnvelope = new ChatEnvelope { KeyDistribution = kd } };
            var ctx = new SessionContext(null, new Percolator.Identity.SelfId(1), null);

            var result = await sut.Handle(new ProcessInternalEnvelopeCommand(env, ctx), CancellationToken.None);
            Assert.That(result, Is.Null);
            mediator.VerifyAll();
        }

        [Test]
        public async Task Chat_EmojiAnnotation_is_dispatched_and_returns_null()
        {
            var mediator = new Mock<IMediator>(MockBehavior.Strict);
            mediator
                .Setup(m => m.Send(It.IsAny<ReceiveEmojiAnnotationCommand>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            var authorSpki = new byte[] { 1, 2, 3 };
            var authorPkh = System.Security.Cryptography.SHA256.HashData(authorSpki);
            var resolvedParticipantId = new Percolator.Chat.ValueObjects.ParticipantId(Guid.NewGuid());
            var pkhPeerResolver = new Mock<Percolator.Chat.App.IPkhPeerResolver>(MockBehavior.Strict);
            pkhPeerResolver
                .Setup(r => r.GetParticipantIdByPkhAsync(
                    Percolator.Chat.ValueObjects.Pkh.FromBytes(authorPkh),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(resolvedParticipantId);

            var sut = CreateSut(mediator, pkhPeerResolver);

            var messageId = Guid.NewGuid();
            var groupId = Guid.NewGuid();
            var em = new EmojiAnnotation
            {
                MessageId = Google.Protobuf.ByteString.CopyFrom(messageId.ToByteArray()),
                Emoji = ":)",
                AuthorIdentityKey = Google.Protobuf.ByteString.CopyFrom(authorSpki),
                SentTimestampUtc = Timestamp.FromDateTime(DateTime.UtcNow)
            };
            em.GroupConversationGuid = Google.Protobuf.ByteString.CopyFrom(groupId.ToByteArray());

            var env = new InternalEnvelope { ChatEnvelope = new ChatEnvelope { EmojiAnnotation = em } };
            var ctx = new SessionContext(null, new Percolator.Identity.SelfId(1), null);

            var result = await sut.Handle(new ProcessInternalEnvelopeCommand(env, ctx), CancellationToken.None);
            Assert.That(result, Is.Null);
            mediator.VerifyAll();
        }

        [Test]
        public async Task Chat_DeliveredReceipt_is_dispatched_and_returns_null()
        {
            var mediator = new Mock<IMediator>(MockBehavior.Strict);
            mediator
                .Setup(m => m.Send(It.IsAny<ReceiveDeliveredReceiptCommand>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            var authorSpki = new byte[] { 1, 2, 3 };
            var authorPkh = System.Security.Cryptography.SHA256.HashData(authorSpki);
            var resolvedParticipantId = new Percolator.Chat.ValueObjects.ParticipantId(Guid.NewGuid());
            var pkhPeerResolver = new Mock<Percolator.Chat.App.IPkhPeerResolver>(MockBehavior.Strict);
            pkhPeerResolver
                .Setup(r => r.GetParticipantIdByPkhAsync(
                    Percolator.Chat.ValueObjects.Pkh.FromBytes(authorPkh),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(resolvedParticipantId);

            var sut = CreateSut(mediator, pkhPeerResolver);

            var messageId = Guid.NewGuid();
            var groupId = Guid.NewGuid();
            var dr = new DeliveredReceipt
            {
                MessageId = Google.Protobuf.ByteString.CopyFrom(messageId.ToByteArray()),
                AuthorIdentityKey = Google.Protobuf.ByteString.CopyFrom(authorSpki),
                SentTimestampUtc = Timestamp.FromDateTime(DateTime.UtcNow)
            };
            dr.GroupConversationGuid = Google.Protobuf.ByteString.CopyFrom(groupId.ToByteArray());

            var env = new InternalEnvelope { ChatEnvelope = new ChatEnvelope { DeliveredReceipt = dr } };
            var ctx = new SessionContext(null, new Percolator.Identity.SelfId(1), null);

            var result = await sut.Handle(new ProcessInternalEnvelopeCommand(env, ctx), CancellationToken.None);
            Assert.That(result, Is.Null);
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
            var adminOps = new Mock<Percolator.Chat.App.IAdminOperations>(MockBehavior.Loose);
            var mq4 = new Moq.Mock<Percolator.MessageQueue.Abstractions.IMessageQueueService>(MockBehavior.Loose);
            var pkhPeerResolver = new Moq.Mock<Percolator.Chat.App.IPkhPeerResolver>(MockBehavior.Loose);
            var sut = new ProcessInternalEnvelopeHandler(logger, mediator.Object, adminOps.Object, dht.Object, mq4.Object, new Mock<Percolator.Network.IPeerRoutingProfileRepository>().Object, pkhPeerResolver.Object);

            // Build InternalEnvelope with DHT FindNodeRequest
            var contractsReq = new Percolator.Contracts.FindNodeRequest
            {
                TargetPeerId = Google.Protobuf.ByteString.CopyFrom(nodeId.ToArray())
            };
            var env = new InternalEnvelope
            {
                DhtEnvelope = new DhtEnvelope { FindNodeRequest = contractsReq }
            };
            var ctx = new SessionContext(null, new Percolator.Identity.SelfId(1), null);

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
            var ctx = new SessionContext(null, new Percolator.Identity.SelfId(1), null);

            var result = await sut.Handle(new ProcessInternalEnvelopeCommand(env, ctx), CancellationToken.None);
            Assert.That(result, Is.Null);
            mediator.VerifyNoOtherCalls();
        }

        [Test]
        public async Task Chat_TextMessage_is_dispatched_and_returns_null()
        {
            var mediator = new Mock<IMediator>(MockBehavior.Strict);
            mediator
                .Setup(m => m.Send(It.IsAny<ReceiveTextMessageCommand>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            var authorSpki = new byte[] { 1, 2, 3 };
            var authorPkh = System.Security.Cryptography.SHA256.HashData(authorSpki);
            var resolvedParticipantId = new Percolator.Chat.ValueObjects.ParticipantId(Guid.NewGuid());
            var pkhPeerResolver = new Mock<Percolator.Chat.App.IPkhPeerResolver>(MockBehavior.Strict);
            pkhPeerResolver
                .Setup(r => r.GetParticipantIdByPkhAsync(
                    Percolator.Chat.ValueObjects.Pkh.FromBytes(authorPkh),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(resolvedParticipantId);

            var sut = CreateSut(mediator, pkhPeerResolver);

            var messageId = Guid.NewGuid();
            var groupId = Guid.NewGuid();
            var text = new TextMessage
            {
                MessageId = Google.Protobuf.ByteString.CopyFrom(messageId.ToByteArray()),
                Content = "hi",
                AuthorIdentityKey = Google.Protobuf.ByteString.CopyFrom(authorSpki),
                SentTimestampUtc = Timestamp.FromDateTime(DateTime.UtcNow)
            };
            text.GroupConversationGuid = Google.Protobuf.ByteString.CopyFrom(groupId.ToByteArray());

            var env = new InternalEnvelope { ChatEnvelope = new ChatEnvelope { TextMessage = text } };
            var ctx = new SessionContext(null, new Percolator.Identity.SelfId(1), null);

            var result = await sut.Handle(new ProcessInternalEnvelopeCommand(env, ctx), CancellationToken.None);
            Assert.That(result, Is.Null);
            mediator.VerifyAll();
        }
    }
}
