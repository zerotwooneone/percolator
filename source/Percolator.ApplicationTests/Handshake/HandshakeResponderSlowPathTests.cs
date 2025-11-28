using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Percolator.Application.Identity;
using Percolator.Application.Network.Handshake;
using Percolator.Application.Sessions;
using Percolator.Application.Services;
using Percolator.Cryptography;
using Percolator.Contracts;
using Google.Protobuf;

namespace Percolator.ApplicationTests.Handshake
{
    [TestFixture]
    public class HandshakeResponderSlowPathTests
    {
        private static async IAsyncEnumerable<PreHandshakeRecord> Empty()
        {
            yield break;
        }

        [Test]
        public async Task SlowPath_Decrypts_Parses_And_CleansPending()
        {
            // Arrange
            var identity = new Percolator.Identity.Model.IdentityRecord(Guid.NewGuid(), "self") { SelfIdentityId = 5 };
            var active = new ActiveIdentityContext { Identity = identity };

            var lookup = new Mock<IRatchetKeyIndex>(MockBehavior.Strict);
            lookup
                .Setup(l => l.TryResolveAsync(It.IsAny<RatchetEphemeralKey>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((SessionId?)null); // force slow-path

            var mostRecent = new PreHandshakeRecord(
                Id: 41,
                SelfIdentityId: identity.SelfIdentityId,
                RecipientPublicKeyHash: new byte[] { 0x41 },
                LocalRequestId: Guid.NewGuid(),
                InitiatorEphemeralPrivateKey: Array.Empty<byte>(),
                InitialRootKey: new byte[] { 0x10, 0x20 },
                CreatedAtUtc: DateTimeOffset.UtcNow,
                ExpiresAtUtc: DateTimeOffset.UtcNow.AddMinutes(5),
                RemoteIdentityKeySpki: new byte[] { 0x90 });

            var preStore = new Mock<IPreHandshakeSessionStore>(MockBehavior.Strict);
            preStore
                .Setup(s => s.EnumeratePendingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .Returns((int _, CancellationToken __) => Empty());
            preStore
                .Setup(s => s.TryGetMostRecentAsync(identity.SelfIdentityId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(mostRecent);
            preStore
                .Setup(s => s.DeleteAsync(mostRecent.Id, identity.SelfIdentityId, It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            var expectedSid = new SessionId(Guid.NewGuid());
            var secure = new Mock<ISecureMessagingService>(MockBehavior.Strict);
            // Serialize ResponderInnerHello directly (it's not inside InternalEnvelope per proto)
            secure
                .Setup(s => s.DecryptInboundAsync(It.IsAny<SessionRatchetMessage>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => {
                    var inner = new ResponderInnerHello
                    {
                        Version = 1,
                        DirectSessionId = expectedSid.Value.ToString()
                    };
                    return (expectedSid, new Plaintext(inner.ToByteArray()));
                });

            var sut = new HandleHandshakeResponderHelloHandler(
                new NullLogger<HandleHandshakeResponderHelloHandler>(),
                secure.Object,
                active,
                lookup.Object,
                preStore.Object);

            var headerPk = new RatchetEphemeralKey(new byte[] { 0xE1 });
            var payload = SessionRatchetMessage.Create(headerPk, 1, 0, new Ciphertext(new byte[] { 0xF1 })).Value;
            var cmd = new HandleHandshakeResponderHelloCommand(payload);

            // Act
            await sut.Handle(cmd, CancellationToken.None);

            // Assert
            secure.Verify(s => s.DecryptInboundAsync(It.IsAny<SessionRatchetMessage>(), It.IsAny<CancellationToken>()), Times.Once);
            preStore.Verify(s => s.DeleteAsync(mostRecent.Id, identity.SelfIdentityId, It.IsAny<CancellationToken>()), Times.Once);
            lookup.VerifyAll();
        }
    }
}
