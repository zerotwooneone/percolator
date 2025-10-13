using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using MediatR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Percolator.Application.Network;
using Percolator.Contracts;
using Percolator.Dht;
using DhtMessages = Percolator.Dht.Messages;
using Percolator.Chat.App.Commands;
using Percolator.Application.Apps.Chat;
using Google.Protobuf.WellKnownTypes;

namespace Percolator.ApplicationTests.Network
{
    public class ProcessInternalEnvelopeHandlerTests
    {
        private static ProcessInternalEnvelopeHandler CreateSut(Mock<IMediator> mediatorMock)
        {
            var logger = NullLogger<ProcessInternalEnvelopeHandler>.Instance;
            return new ProcessInternalEnvelopeHandler(logger, mediatorMock.Object);
        }

        [Test]
        public async Task Chat_ReadReceipt_is_dispatched_and_returns_null()
        {
            var mediator = new Mock<IMediator>(MockBehavior.Strict);
            mediator
                .Setup(m => m.Send(It.IsAny<PostReadReceiptCommand>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            var sut = CreateSut(mediator);

            var messageId = Guid.NewGuid();
            var groupId = Guid.NewGuid();
            var rr = new ReadReceipt
            {
                MessageId = Google.Protobuf.ByteString.CopyFrom(messageId.ToByteArray()),
                SentTimestampUtc = Timestamp.FromDateTime(DateTime.UtcNow)
            };
            rr.GroupConversationGuid = Google.Protobuf.ByteString.CopyFrom(groupId.ToByteArray());

            var env = new InternalEnvelope { ChatEnvelope = new ChatEnvelope { ReadReceipt = rr } };
            var ctx = new SessionContext(null, 1, null);

            var result = await sut.Handle(new ProcessInternalEnvelopeCommand(env, ctx), CancellationToken.None);
            Assert.That(result, Is.Null);
            mediator.VerifyAll();
        }

        [Test]
        public async Task Chat_SignedAdminOperation_is_dispatched_and_returns_null()
        {
            var mediator = new Mock<IMediator>(MockBehavior.Strict);
            mediator
                .Setup(m => m.Send(It.IsAny<ApplySignedAdminOperationCommand>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            var sut = CreateSut(mediator);

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
            var ctx = new SessionContext(null, 1, null);

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
            var ctx = new SessionContext(null, 1, null);

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
            var ctx = new SessionContext(null, 1, null);

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
            var ctx = new SessionContext(null, 1, null);

            var result = await sut.Handle(new ProcessInternalEnvelopeCommand(env, ctx), CancellationToken.None);
            Assert.That(result, Is.Null);
            mediator.VerifyAll();
        }

        [Test]
        public async Task Chat_EmojiAnnotation_is_dispatched_and_returns_null()
        {
            var mediator = new Mock<IMediator>(MockBehavior.Strict);
            mediator
                .Setup(m => m.Send(It.IsAny<PostEmojiAnnotationCommand>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            var sut = CreateSut(mediator);

            var messageId = Guid.NewGuid();
            var groupId = Guid.NewGuid();
            var em = new EmojiAnnotation
            {
                MessageId = Google.Protobuf.ByteString.CopyFrom(messageId.ToByteArray()),
                Emoji = ":)",
                SentTimestampUtc = Timestamp.FromDateTime(DateTime.UtcNow)
            };
            em.GroupConversationGuid = Google.Protobuf.ByteString.CopyFrom(groupId.ToByteArray());

            var env = new InternalEnvelope { ChatEnvelope = new ChatEnvelope { EmojiAnnotation = em } };
            var ctx = new SessionContext(null, 1, null);

            var result = await sut.Handle(new ProcessInternalEnvelopeCommand(env, ctx), CancellationToken.None);
            Assert.That(result, Is.Null);
            mediator.VerifyAll();
        }

        [Test]
        public async Task Chat_DeliveredReceipt_is_dispatched_and_returns_null()
        {
            var mediator = new Mock<IMediator>(MockBehavior.Strict);
            mediator
                .Setup(m => m.Send(It.IsAny<PostDeliveredReceiptCommand>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            var sut = CreateSut(mediator);

            var messageId = Guid.NewGuid();
            var groupId = Guid.NewGuid();
            var dr = new DeliveredReceipt
            {
                MessageId = Google.Protobuf.ByteString.CopyFrom(messageId.ToByteArray()),
                SentTimestampUtc = Timestamp.FromDateTime(DateTime.UtcNow)
            };
            dr.GroupConversationGuid = Google.Protobuf.ByteString.CopyFrom(groupId.ToByteArray());

            var env = new InternalEnvelope { ChatEnvelope = new ChatEnvelope { DeliveredReceipt = dr } };
            var ctx = new SessionContext(null, 1, null);

            var result = await sut.Handle(new ProcessInternalEnvelopeCommand(env, ctx), CancellationToken.None);
            Assert.That(result, Is.Null);
            mediator.VerifyAll();
        }

        [Test]
        public async Task FindNodeRequest_returns_response_envelope()
        {
            var mediator = new Mock<IMediator>(MockBehavior.Strict);

            // Arrange DHT mediator response
            var nodeId = new NodeId(new byte[] { 1,2,3,4, 5,6,7,8, 9,10,11,12, 13,14,15,16,
                                                  17,18,19,20, 21,22,23,24, 25,26,27,28, 29,30,31,32 });
            var dns = new DnsEndPoint("127.0.0.1", 3030);
            var dhtNode = new DhtNode(nodeId, dns, DateTimeOffset.UtcNow);
            mediator
                .Setup(m => m.Send(It.IsAny<DhtMessages.FindNodeRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new DhtMessages.FindNodeResponse(new[] { dhtNode }));

            var sut = CreateSut(mediator);

            // Build InternalEnvelope with DHT FindNodeRequest
            var contractsReq = new Percolator.Contracts.FindNodeRequest
            {
                TargetPeerId = Google.Protobuf.ByteString.CopyFrom(nodeId.Value)
            };
            var env = new InternalEnvelope
            {
                DhtEnvelope = new DhtEnvelope { FindNodeRequest = contractsReq }
            };
            var ctx = new SessionContext(null, 1, null);

            // Act
            var result = await sut.Handle(new ProcessInternalEnvelopeCommand(env, ctx), CancellationToken.None);

            // Assert
            Assert.That(result, Is.Not.Null);
            Assert.That(result!.ApplicationPayloadCase, Is.EqualTo(InternalEnvelope.ApplicationPayloadOneofCase.DhtEnvelope));
            Assert.That(result.DhtEnvelope.FindNodeResponse, Is.Not.Null);
            Assert.That(result.DhtEnvelope.FindNodeResponse.CloserPeers.Count, Is.EqualTo(1));
            Assert.That(result.DhtEnvelope.FindNodeResponse.CloserPeers[0].Address, Is.EqualTo("127.0.0.1:3030"));

            mediator.VerifyAll();
        }

        [Test]
        public async Task NonDht_returns_null()
        {
            var mediator = new Mock<IMediator>(MockBehavior.Strict);
            var sut = CreateSut(mediator);

            var env = new InternalEnvelope { ChatEnvelope = new ChatEnvelope { Version = 1 } };
            var ctx = new SessionContext(null, 1, null);

            var result = await sut.Handle(new ProcessInternalEnvelopeCommand(env, ctx), CancellationToken.None);
            Assert.That(result, Is.Null);
            mediator.VerifyNoOtherCalls();
        }

        [Test]
        public async Task Chat_TextMessage_is_dispatched_and_returns_null()
        {
            var mediator = new Mock<IMediator>(MockBehavior.Strict);
            mediator
                .Setup(m => m.Send(It.IsAny<PostTextMessageCommand>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            var sut = CreateSut(mediator);

            var messageId = Guid.NewGuid();
            var groupId = Guid.NewGuid();
            var text = new TextMessage
            {
                MessageId = Google.Protobuf.ByteString.CopyFrom(messageId.ToByteArray()),
                Content = "hi",
                SentTimestampUtc = Timestamp.FromDateTime(DateTime.UtcNow)
            };
            text.GroupConversationGuid = Google.Protobuf.ByteString.CopyFrom(groupId.ToByteArray());

            var env = new InternalEnvelope { ChatEnvelope = new ChatEnvelope { TextMessage = text } };
            var ctx = new SessionContext(null, 1, null);

            var result = await sut.Handle(new ProcessInternalEnvelopeCommand(env, ctx), CancellationToken.None);
            Assert.That(result, Is.Null);
            mediator.VerifyAll();
        }
    }
}
