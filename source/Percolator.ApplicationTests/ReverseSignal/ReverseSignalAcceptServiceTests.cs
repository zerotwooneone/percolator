using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Google.Protobuf;
using Moq;
using NUnit.Framework;
using Percolator.Application.Network;
using Percolator.Application.ReverseSignal;
using Percolator.Application.Network.Handshake;
using Percolator.Contracts;
using Percolator.Cryptography;
using Percolator.Identity;
using Microsoft.Extensions.Logging;

namespace Percolator.ApplicationTests.ReverseSignal
{
    [TestFixture]
    public class ReverseSignalAcceptServiceTests
    {
        private static HandshakeInitiatorHello BuildHello()
        {
            // Minimal valid hello
            return new HandshakeInitiatorHello
            {
                InitiatorIdentityKeySpki = Google.Protobuf.ByteString.CopyFrom(new byte[] { 0x01 }),
                InitiatorEphemeralKeySpki = Google.Protobuf.ByteString.CopyFrom(new byte[] { 0x02 }),
                SignedPreKeyId = Google.Protobuf.ByteString.CopyFrom(Guid.NewGuid().ToByteArray())
            };
        }

        private static PendingSession BuildPending(HandshakeInitiatorHello hello)
        {
            var clock = new TestClock { UtcNow = DateTimeOffset.UtcNow };
            var inv = new HandshakeInvitation(hello.ToByteArray());
            return PendingSession.FromInvitation(
                PendingSessionId.NewId(),
               Percolator.Cryptography.Primitives.PeerId.NewId(),
                new ProtocolVersion(1),
                inv,
                clock,
                expiresAtUtc: clock.UtcNow.AddMinutes(5));
        }

        private sealed class TestClock : IClock { public DateTimeOffset UtcNow { get; set; } }

        [Test]
        public async Task AcceptAsync_SendsResponderHello_And_DeletesPending_OnSuccess()
        {
            // Arrange
            var hello = BuildHello();
            var pending = BuildPending(hello);
            var logger = Mock.Of<ILogger<ReverseSignalAcceptService>>();
            var repo = new Mock<IPendingSessionRepository>(MockBehavior.Strict);
            repo.Setup(r => r.GetAsync(pending.Id, It.IsAny<CancellationToken>()))
                .ReturnsAsync(pending);
            repo.Setup(r => r.DeleteAsync(pending.Id, It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            var mediator = new Mock<MediatR.IMediator>(MockBehavior.Strict);
            var remotePeer = Percolator.Identity.PeerId.NewId();
            var cipher = new SessionRatchetMessage(new byte[] { 0xAA });
            mediator.Setup(m => m.Send(It.IsAny<HandleHandshakeInitiatorHelloCommand>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new HandleHandshakeInitiatorHelloResult(remotePeer, cipher));

            var msg = new Mock<IMessageService>(MockBehavior.Strict);
            msg.Setup(s => s.SendPreEncryptedAsync(remotePeer, cipher, It.IsAny<CancellationToken>()))
                .ReturnsAsync(SendResult.CreateSuccess("direct", new[] { "direct" }, 1));

            var svc = new ReverseSignalAcceptService(logger, repo.Object, mediator.Object, msg.Object);

            // Act
            var ok = await svc.AcceptAsync(pending.Id);

            // Assert
            ok.Should().BeTrue();
            repo.Verify(r => r.DeleteAsync(pending.Id, It.IsAny<CancellationToken>()), Times.Once);
            mediator.VerifyAll();
            msg.VerifyAll();
            repo.VerifyAll();
        }

        [Test]
        public async Task AcceptAsync_KeepsPending_WhenSendFails()
        {
            // Arrange
            var hello = BuildHello();
            var pending = BuildPending(hello);
            var logger = Mock.Of<ILogger<ReverseSignalAcceptService>>();
            var repo = new Mock<IPendingSessionRepository>(MockBehavior.Strict);
            repo.Setup(r => r.GetAsync(pending.Id, It.IsAny<CancellationToken>()))
                .ReturnsAsync(pending);

            var mediator = new Mock<MediatR.IMediator>(MockBehavior.Strict);
            var remotePeer = Percolator.Identity.PeerId.NewId();
            var cipher = new SessionRatchetMessage(new byte[] { 0xAB });
            mediator.Setup(m => m.Send(It.IsAny<HandleHandshakeInitiatorHelloCommand>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new HandleHandshakeInitiatorHelloResult(remotePeer, cipher));

            var msg = new Mock<IMessageService>(MockBehavior.Strict);
            msg.Setup(s => s.SendPreEncryptedAsync(remotePeer, cipher, It.IsAny<CancellationToken>()))
                .ReturnsAsync(SendResult.CreateFailure(new[] { "direct", "relay" }, 2, new Exception("network")));

            var svc = new ReverseSignalAcceptService(logger, repo.Object, mediator.Object, msg.Object);

            // Act
            var ok = await svc.AcceptAsync(pending.Id);

            // Assert
            ok.Should().BeFalse();
            repo.Verify(r => r.DeleteAsync(It.IsAny<PendingSessionId>(), It.IsAny<CancellationToken>()), Times.Never);
            mediator.VerifyAll();
            msg.VerifyAll();
            repo.VerifyAll();
        }
    }
}
