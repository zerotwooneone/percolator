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
            var ratchetIndex = new Mock<IRatchetKeyIndex>(MockBehavior.Strict);
            var active = new ActiveIdentityContext { Identity = new Percolator.Identity.Model.IdentityRecord(Guid.NewGuid(), "self") { SelfIdentityId = new SelfId(1) } };
            var activeAccessor = Mock.Of<IActiveIdentityAccessor>(a => a.IsActive == true);

            var expectedSid = new SessionId(Guid.NewGuid());
            ratchetIndex
                .Setup(x => x.TryResolveAsync(It.IsAny<RatchetEphemeralKey>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(expectedSid);

            var finalize1 = new Moq.Mock<IInitiatorFinalizeService>(Moq.MockBehavior.Strict);
            var handler = new HandleHandshakeResponderHelloHandler(
                new NullLogger<HandleHandshakeResponderHelloHandler>(),
                activeAccessor,
                active,
                ratchetIndex.Object,
                finalize1.Object);

            // Build a valid ratchet message payload (header present)
            var pk = new RatchetEphemeralKey(new byte[] { 0xA1 });
            var payload = SessionRatchetMessage.Create(pk, 1, 0, new Ciphertext(new byte[] { 0x01 })).Value;
            await handler.Handle(new HandleHandshakeResponderHelloCommand(payload), CancellationToken.None);

        }

        [Test]
        public async Task SlowPath_FinalizeFromPrehandshake_And_Delete()
        {
            var ratchetIndex = new Mock<IRatchetKeyIndex>(MockBehavior.Strict);
            var active = new ActiveIdentityContext { Identity = new Percolator.Identity.Model.IdentityRecord(Guid.NewGuid(), "self") { SelfIdentityId = new SelfId(2) } };
            var activeAccessor = Mock.Of<IActiveIdentityAccessor>(a => a.IsActive == true);

            ratchetIndex
                .Setup(x => x.TryResolveAsync(It.IsAny<RatchetEphemeralKey>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((SessionId?)null);

            var sid = new SessionId(Guid.NewGuid());
            var inner = MakeInner(sid.Value);
            var plaintext = new Plaintext(inner.ToByteArray());
            var finalize = new Moq.Mock<IInitiatorFinalizeService>(Moq.MockBehavior.Strict);
            finalize
                .Setup(f => f.TryFinalizeFromFirstResponderAsync(It.IsAny<SessionRatchetMessage>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((sid, plaintext));

            var pre = new PreHandshakeRecord(
                Id: 42,
                SelfIdentityId: active.Identity!.SelfIdentityId.Value,
                RecipientPublicKeyHash: new byte[] { 1 },
                LocalRequestId: Guid.NewGuid(),
                InitiatorEphemeralPrivateKey: Array.Empty<byte>(),
                InitialRootKey: new byte[] { 2, 3 },
                CreatedAtUtc: DateTimeOffset.UtcNow,
                ExpiresAtUtc: DateTimeOffset.UtcNow.AddMinutes(10),
                RemoteIdentityKeySpki: new byte[] { 9, 9, 9 });

            // No preStore expectations in this test; finalize service abstracts cleanup

            var handler = new HandleHandshakeResponderHelloHandler(
                new NullLogger<HandleHandshakeResponderHelloHandler>(),
                activeAccessor,
                active,
                ratchetIndex.Object,
                finalize.Object);

            // Valid ratchet message (header present)
            var pk2 = new RatchetEphemeralKey(new byte[] { 0xB1 });
            var payload2 = SessionRatchetMessage.Create(pk2, 1, 0, new Ciphertext(new byte[] { 0x02 })).Value;
            await handler.Handle(new HandleHandshakeResponderHelloCommand(payload2), CancellationToken.None);

            finalize.Verify(f => f.TryFinalizeFromFirstResponderAsync(It.IsAny<SessionRatchetMessage>(), It.IsAny<CancellationToken>()), Times.Once);
        }
    }
}
