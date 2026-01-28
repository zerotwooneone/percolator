using System;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Percolator.Application.Identity;
using Percolator.Application.Network.Handshake;
using Percolator.Application.Sessions;
using Percolator.Application.Services;
using Percolator.Contracts;
using Percolator.Cryptography;
using Percolator.Identity;

namespace Percolator.ApplicationTests.Network
{
    [TestFixture]
    public class InboundResolutionParityTests
    {
        private static ResponderInnerHello MakeInner(Guid sid)
        {
            return new ResponderInnerHello
            {
                Version = 1,
                DirectSessionId = sid.ToString()
            };
        }

        [Test]
        public async Task FastPath_UsesRatchetIndex_DoesNotFinalize()
        {
            // Arrange
            var ratchetIndex = new Mock<IRatchetKeyIndex>(MockBehavior.Strict);
            var active = new ActiveIdentityContext
            {
                Identity = new Percolator.Identity.Model.IdentityRecord(Guid.NewGuid(), "self")
                {
                    SelfIdentityId = new SelfId(1)
                }
            };
            var activeAccessor = Mock.Of<IActiveIdentityAccessor>(a => a.IsActive == true);

            var expectedSid = new SessionId(Guid.NewGuid());
            ratchetIndex
                .Setup(x => x.TryResolveAsync(It.IsAny<RatchetEphemeralKey>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(expectedSid);

            var finalize = new Mock<IInitiatorFinalizeService>(MockBehavior.Loose);
            var handler = new HandleHandshakeResponderHelloHandler(
                new NullLogger<HandleHandshakeResponderHelloHandler>(),
                activeAccessor,
                active,
                ratchetIndex.Object,
                finalize.Object);

            var pk = new RatchetEphemeralKey(new byte[] { 0xA1 });
            var payload = SessionRatchetMessage.Create(pk, 1, 0, new Ciphertext(new byte[] { 0x01 })).Value;

            var resp = new InviteHandshakeResponse
            {
                Version = 1,
                RequestCorrelationId = Guid.NewGuid().ToString(),
                AcceptorIdentityKey = ByteString.CopyFrom(new byte[] { 0x01 }),
                AcceptorX3DhEphemeralKey = ByteString.CopyFrom(new byte[] { 0x02 }),
                InitialRatchetMessage = ByteString.CopyFrom(payload)
            };

            var cmd = new HandleHandshakeResponderHelloCommand(resp);

            // Act
            await handler.Handle(cmd, CancellationToken.None);

            // Assert
            ratchetIndex.Verify(x => x.TryResolveAsync(It.IsAny<RatchetEphemeralKey>(), It.IsAny<CancellationToken>()), Times.Once);
            finalize.Verify(
                f => f.TryFinalizeFromInviteHandshakeResponseAsync(It.IsAny<InviteHandshakeResponse>(), It.IsAny<CancellationToken>()),
                Times.Never);
            finalize.Verify(
                f => f.TryFinalizeFromFirstResponderAsync(It.IsAny<SessionRatchetMessage>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }

        [Test]
        public async Task SlowPath_FinalizeFromPrehandshake_And_Delete()
        {
            // Arrange
            var ratchetIndex = new Mock<IRatchetKeyIndex>(MockBehavior.Strict);
            var active = new ActiveIdentityContext
            {
                Identity = new Percolator.Identity.Model.IdentityRecord(Guid.NewGuid(), "self")
                {
                    SelfIdentityId = new SelfId(2)
                }
            };
            var activeAccessor = Mock.Of<IActiveIdentityAccessor>(a => a.IsActive == true);

            ratchetIndex
                .Setup(x => x.TryResolveAsync(It.IsAny<RatchetEphemeralKey>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((SessionId?)null);

            var sid = new SessionId(Guid.NewGuid());
            var inner = MakeInner(sid.Value);
            var plaintext = new Plaintext(inner.ToByteArray());

            var finalize = new Mock<IInitiatorFinalizeService>(MockBehavior.Strict);
            finalize
                .Setup(f => f.TryFinalizeFromInviteHandshakeResponseAsync(It.IsAny<InviteHandshakeResponse>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(((SessionId sessionId, Plaintext plaintext)?)null);
            finalize
                .Setup(f => f.TryFinalizeFromFirstResponderAsync(It.IsAny<SessionRatchetMessage>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((sid, plaintext));

            var handler = new HandleHandshakeResponderHelloHandler(
                new NullLogger<HandleHandshakeResponderHelloHandler>(),
                activeAccessor,
                active,
                ratchetIndex.Object,
                finalize.Object);

            var pk2 = new RatchetEphemeralKey(new byte[] { 0xB1 });
            var payload2 = SessionRatchetMessage.Create(pk2, 1, 0, new Ciphertext(new byte[] { 0x02 })).Value;

            var resp = new InviteHandshakeResponse
            {
                Version = 1,
                RequestCorrelationId = Guid.NewGuid().ToString(),
                AcceptorIdentityKey = ByteString.CopyFrom(new byte[] { 0x01 }),
                AcceptorX3DhEphemeralKey = ByteString.CopyFrom(new byte[] { 0x02 }),
                InitialRatchetMessage = ByteString.CopyFrom(payload2)
            };

            var cmd = new HandleHandshakeResponderHelloCommand(resp);

            // Act
            await handler.Handle(cmd, CancellationToken.None);

            // Assert
            ratchetIndex.Verify(x => x.TryResolveAsync(It.IsAny<RatchetEphemeralKey>(), It.IsAny<CancellationToken>()), Times.Once);
            finalize.Verify(
                f => f.TryFinalizeFromInviteHandshakeResponseAsync(It.IsAny<InviteHandshakeResponse>(), It.IsAny<CancellationToken>()),
                Times.Once);
            finalize.Verify(
                f => f.TryFinalizeFromFirstResponderAsync(It.IsAny<SessionRatchetMessage>(), It.IsAny<CancellationToken>()),
                Times.Once);
            finalize.VerifyNoOtherCalls();
        }
    }
}
