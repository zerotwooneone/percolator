using System;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Percolator.Application.Network.Handshake;
using Percolator.Application.Sessions;
using Percolator.Application.Services;
using Percolator.Contracts;
using Percolator.Cryptography;
using Percolator.Identity;

namespace Percolator.ApplicationTests.Handshake
{
    [TestFixture]
    public class HandshakeResponderFastPathTests
    {
        private static async IAsyncEnumerable<PreHandshakeRecord> Empty()
        {
            yield break;
        }
        [Test]
        public async Task FastPath_UsesRatchetIndexLookup()
        {
            // Arrange
            var selfId = new SelfId(1);

            var lookup = new Mock<IRatchetKeyIndex>(MockBehavior.Strict);
            var resolved = new SessionId(Guid.NewGuid());
            lookup
                .Setup(l => l.TryResolveAsync(It.IsAny<int>(), It.IsAny<RatchetEphemeralKey>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(resolved);

            var finalize = new Mock<IInitiatorFinalizeService>(MockBehavior.Strict);

            finalize
                .Setup(f => f.TryFinalizeFromInviteHandshakeResponseAsync(It.IsAny<SelfId>(), It.IsAny<InviteHandshakeResponse>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(((SessionId sessionId, Plaintext plaintext)?)null);

            finalize
                .Setup(f => f.TryFinalizeFromFirstResponderAsync(It.IsAny<SelfId>(), It.IsAny<SessionRatchetMessage>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(((SessionId sessionId, Plaintext plaintext)?)null);

            var handler = new HandleHandshakeResponderHelloHandler(
                new NullLogger<HandleHandshakeResponderHelloHandler>(),
                lookup.Object,
                finalize.Object);

            var pk = new RatchetEphemeralKey(new byte[] { 0xAA });
            var payload = SessionRatchetMessage.Create(pk, 1, 0, new Ciphertext(new byte[] { 0xBB })).Value;
            var resp = new InviteHandshakeResponse
            {
                Version = 1,
                RequestCorrelationId = Guid.NewGuid().ToString(),
                AcceptorIdentityKey = ByteString.CopyFrom(new byte[] { 0x01 }),          // any non-empty
                AcceptorX3DhEphemeralKey = ByteString.CopyFrom(new byte[] { 0x02 }),      // any non-empty
                InitialRatchetMessage = ByteString.CopyFrom(payload)
            };

            var cmd = new HandleHandshakeResponderHelloCommand(selfId, resp);

            // Act
            await handler.Handle(cmd, CancellationToken.None);

            // Assert
            lookup.Verify(l => l.TryResolveAsync(It.IsAny<int>(), It.IsAny<RatchetEphemeralKey>(), It.IsAny<CancellationToken>()), Times.Once);
            lookup.VerifyAll();
        }
    }
}
