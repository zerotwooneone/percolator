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
            var sessions = new Mock<IDirectSessionManager>(MockBehavior.Strict);
            var secure = new Mock<ISecureMessagingService>(MockBehavior.Strict);
            var ratchetIndex = new Mock<IRatchetKeyIndex>(MockBehavior.Strict);
            var preStore = new Mock<IPreHandshakeSessionStore>(MockBehavior.Strict);
            var active = new ActiveIdentityContext { Identity = new Percolator.Identity.Model.IdentityRecord(Guid.NewGuid(), "self") { SelfIdentityId = 1 } };

            var expectedSid = new SessionId(Guid.NewGuid());
            ratchetIndex
                .Setup(x => x.TryResolveAsync(It.IsAny<RatchetEphemeralKey>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(expectedSid);

            var handler = new HandleHandshakeResponderHelloHandler(
                new NullLogger<HandleHandshakeResponderHelloHandler>(),
                sessions.Object,
                secure.Object,
                active,
                ratchetIndex.Object,
                preStore.Object);

            // Build a valid ratchet message payload (header present)
            var pk = new RatchetEphemeralKey(new byte[] { 0xA1 });
            var payload = SessionRatchetMessage.Create(pk, 1, 0, new Ciphertext(new byte[] { 0x01 })).Value;
            await handler.Handle(new HandleHandshakeResponderHelloCommand(payload), CancellationToken.None);

            // Verify no finalize or prehandshake access occurred
            sessions.VerifyNoOtherCalls();
            preStore.VerifyNoOtherCalls();
        }

        [Test]
        public async Task SlowPath_FinalizeFromPrehandshake_And_Delete()
        {
            var sessions = new Mock<IDirectSessionManager>(MockBehavior.Strict);
            var secure = new Mock<ISecureMessagingService>(MockBehavior.Strict);
            var ratchetIndex = new Mock<IRatchetKeyIndex>(MockBehavior.Strict);
            var preStore = new Mock<IPreHandshakeSessionStore>(MockBehavior.Strict);
            var active = new ActiveIdentityContext { Identity = new Percolator.Identity.Model.IdentityRecord(Guid.NewGuid(), "self") { SelfIdentityId = 2 } };

            ratchetIndex
                .Setup(x => x.TryResolveAsync(It.IsAny<RatchetEphemeralKey>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((SessionId?)null);

            var sid = new SessionId(Guid.NewGuid());
            var inner = MakeInner(sid.Value);
            var plaintext = new Plaintext(inner.ToByteArray());
            secure
                .Setup(s => s.DecryptInboundAsync(It.IsAny<SessionRatchetMessage>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((sid, plaintext));

            var pre = new PreHandshakeRecord(
                Id: 42,
                SelfIdentityId: active.Identity!.SelfIdentityId,
                RecipientPublicKeyHash: new byte[] { 1 },
                LocalRequestId: Guid.NewGuid(),
                InitiatorEphemeralPrivateKey: Array.Empty<byte>(),
                InitialRootKey: new byte[] { 2, 3 },
                CreatedAtUtc: DateTimeOffset.UtcNow,
                ExpiresAtUtc: DateTimeOffset.UtcNow.AddMinutes(10),
                RemoteIdentityKeySpki: new byte[] { 9, 9, 9 });

            preStore
                .Setup(s => s.TryGetMostRecentAsync(active.Identity!.SelfIdentityId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(pre);

            sessions
                .Setup(s => s.FinalizeAsInitiatorAsync(
                    sid,
                    It.IsAny<RatchetIdentityKey>(),
                    It.Is<SharedSecret>(ss => ss.Value.Length == pre.InitialRootKey.Length),
                    It.IsAny<RatchetEphemeralKey>()))
                .Returns(Task.CompletedTask);

            preStore
                .Setup(s => s.DeleteAsync(pre.Id, active.Identity!.SelfIdentityId, It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            var handler = new HandleHandshakeResponderHelloHandler(
                new NullLogger<HandleHandshakeResponderHelloHandler>(),
                sessions.Object,
                secure.Object,
                active,
                ratchetIndex.Object,
                preStore.Object);

            // Valid ratchet message (header present)
            var pk2 = new RatchetEphemeralKey(new byte[] { 0xB1 });
            var payload2 = SessionRatchetMessage.Create(pk2, 1, 0, new Ciphertext(new byte[] { 0x02 })).Value;
            await handler.Handle(new HandleHandshakeResponderHelloCommand(payload2), CancellationToken.None);

            sessions.VerifyAll();
            preStore.VerifyAll();
        }
    }
}
