using Google.Protobuf;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Percolator.Application.Network.Handshake;
using Percolator.Contracts;
using Percolator.Cryptography;
using Percolator.Cryptography.Primitives;
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
            var selfId = new SelfId(1);

            var expectedSid = new SessionId(Guid.NewGuid());
            ratchetIndex
                .Setup(x => x.TryResolveAsync(It.IsAny<CryptoSelfId>(), It.IsAny<RatchetEphemeralKey>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(expectedSid);

            var finalize = new Mock<IInitiatorFinalizeService>(MockBehavior.Loose);
            var handler = new HandleHandshakeResponderHelloHandler(
                new NullLogger<HandleHandshakeResponderHelloHandler>(),
                ratchetIndex.Object,
                finalize.Object);

            var pk = RatchetEphemeralKey.FromBytes(new byte[64]);
            var payload = SessionRatchetMessage.Create(pk, 1, 0, Ciphertext.FromBytes(new byte[] { 0x01 })).ToArray();

            var resp = new InviteHandshakeResponse
            {
                Version = 1,
                RequestCorrelationId = Guid.NewGuid().ToString(),
                AcceptorIdentityKey = ByteString.CopyFrom(new byte[] { 0x01 }),
                AcceptorX3DhEphemeralKey = ByteString.CopyFrom(new byte[] { 0x02 }),
                InitialRatchetMessage = ByteString.CopyFrom(payload)
            };

            var cmd = new HandleHandshakeResponderHelloCommand(selfId, resp);

            // Act
            await handler.Handle(cmd, CancellationToken.None);

            // Assert
            ratchetIndex.Verify(x => x.TryResolveAsync(It.IsAny<CryptoSelfId>(), It.IsAny<RatchetEphemeralKey>(), It.IsAny<CancellationToken>()), Times.Once);
            finalize.Verify(
                f => f.TryFinalizeFromInviteHandshakeResponseAsync(It.IsAny<SelfId>(), It.IsAny<InviteHandshakeResponse>(), It.IsAny<CancellationToken>()),
                Times.Never);
            finalize.Verify(
                f => f.TryFinalizeFromFirstResponderAsync(It.IsAny<SelfId>(), It.IsAny<SessionRatchetMessage>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }

        [Test]
        public async Task SlowPath_FinalizeFromPrehandshake_And_Delete()
        {
            // Arrange
            var ratchetIndex = new Mock<IRatchetKeyIndex>(MockBehavior.Strict);
            var selfId = new SelfId(2);

            ratchetIndex
                .Setup(x => x.TryResolveAsync(It.IsAny<CryptoSelfId>(), It.IsAny<RatchetEphemeralKey>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((SessionId?)null);

            var sid = new SessionId(Guid.NewGuid());
            var inner = MakeInner(sid.Value);
            var plaintext = Plaintext.FromBytes(inner.ToByteArray());

            var finalize = new Mock<IInitiatorFinalizeService>(MockBehavior.Strict);
            finalize
                .Setup(f => f.TryFinalizeFromInviteHandshakeResponseAsync(It.IsAny<SelfId>(), It.IsAny<InviteHandshakeResponse>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(((SessionId sessionId, Plaintext plaintext)?)null);
            finalize
                .Setup(f => f.TryFinalizeFromFirstResponderAsync(It.IsAny<SelfId>(), It.IsAny<SessionRatchetMessage>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((sid, plaintext));

            var handler = new HandleHandshakeResponderHelloHandler(
                new NullLogger<HandleHandshakeResponderHelloHandler>(),
                ratchetIndex.Object,
                finalize.Object);

            var pk2 = RatchetEphemeralKey.FromBytes(new byte[64]);
            var payload2 = SessionRatchetMessage.Create(pk2, 1, 0, Ciphertext.FromBytes(new byte[] { 0x02 })).ToArray();

            var resp = new InviteHandshakeResponse
            {
                Version = 1,
                RequestCorrelationId = Guid.NewGuid().ToString(),
                AcceptorIdentityKey = ByteString.CopyFrom(new byte[] { 0x01 }),
                AcceptorX3DhEphemeralKey = ByteString.CopyFrom(new byte[] { 0x02 }),
                InitialRatchetMessage = ByteString.CopyFrom(payload2)
            };

            var cmd = new HandleHandshakeResponderHelloCommand(selfId, resp);

            // Act
            await handler.Handle(cmd, CancellationToken.None);

            // Assert
            ratchetIndex.Verify(x => x.TryResolveAsync(It.IsAny<CryptoSelfId>(), It.IsAny<RatchetEphemeralKey>(), It.IsAny<CancellationToken>()), Times.Once);
            finalize.Verify(
                f => f.TryFinalizeFromInviteHandshakeResponseAsync(It.IsAny<SelfId>(), It.IsAny<InviteHandshakeResponse>(), It.IsAny<CancellationToken>()),
                Times.Once);
            finalize.Verify(
                f => f.TryFinalizeFromFirstResponderAsync(It.IsAny<SelfId>(), It.IsAny<SessionRatchetMessage>(), It.IsAny<CancellationToken>()),
                Times.Once);
            finalize.VerifyNoOtherCalls();
        }
    }
}
