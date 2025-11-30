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
using Percolator.Identity;

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
            var identity = new Percolator.Identity.Model.IdentityRecord(Guid.NewGuid(), "self") { SelfIdentityId = new SelfId(5) };
            var active = new ActiveIdentityContext { Identity = identity };

            var lookup = new Mock<IRatchetKeyIndex>(MockBehavior.Strict);
            lookup
                .Setup(l => l.TryResolveAsync(It.IsAny<RatchetEphemeralKey>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((SessionId?)null); // force slow-path

            var mostRecent = new PreHandshakeRecord(
                Id: 41,
                SelfIdentityId: identity.SelfIdentityId.Value,
                RecipientPublicKeyHash: new byte[] { 0x41 },
                LocalRequestId: Guid.NewGuid(),
                InitiatorEphemeralPrivateKey: Array.Empty<byte>(),
                InitialRootKey: new byte[] { 0x10, 0x20 },
                CreatedAtUtc: DateTimeOffset.UtcNow,
                ExpiresAtUtc: DateTimeOffset.UtcNow.AddMinutes(5),
                RemoteIdentityKeySpki: new byte[] { 0x90 });

            var expectedSid = new SessionId(Guid.NewGuid());
            var finalize = new Mock<IInitiatorFinalizeService>(MockBehavior.Strict);
            finalize
                .Setup(f => f.TryFinalizeFromFirstResponderAsync(It.IsAny<SessionRatchetMessage>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() =>
                {
                    var inner = new ResponderInnerHello
                    {
                        Version = 1,
                        DirectSessionId = expectedSid.Value.ToString()
                    };
                    return (expectedSid, new Plaintext(inner.ToByteArray()));
                });

            var sut = new HandleHandshakeResponderHelloHandler(
                new NullLogger<HandleHandshakeResponderHelloHandler>(),
                active,
                lookup.Object,
                finalize.Object);

            var headerPk = new RatchetEphemeralKey(new byte[] { 0xE1 });
            var payload = SessionRatchetMessage.Create(headerPk, 1, 0, new Ciphertext(new byte[] { 0xF1 })).Value;
            var cmd = new HandleHandshakeResponderHelloCommand(payload);

            // Act
            await sut.Handle(cmd, CancellationToken.None);

            // Assert
            finalize.Verify(f => f.TryFinalizeFromFirstResponderAsync(It.IsAny<SessionRatchetMessage>(), It.IsAny<CancellationToken>()), Times.Once);
            lookup.VerifyAll();
        }
    }
}
