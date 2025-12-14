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
            var identity = new Percolator.Identity.Model.IdentityRecord(Guid.NewGuid(), "self") { SelfIdentityId = new SelfId(1) };
            var active = new ActiveIdentityContext { Identity = identity };
            var activeAccessor = Mock.Of<IActiveIdentityAccessor>(a => a.IsActive == true);

            var lookup = new Mock<IRatchetKeyIndex>(MockBehavior.Strict);
            var resolved = new SessionId(Guid.NewGuid());
            lookup
                .Setup(l => l.TryResolveAsync(It.IsAny<RatchetEphemeralKey>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(resolved);

            var finalize = new Mock<IInitiatorFinalizeService>(MockBehavior.Strict);

            var handler = new HandleHandshakeResponderHelloHandler(
                new NullLogger<HandleHandshakeResponderHelloHandler>(),
                activeAccessor,
                active,
                lookup.Object,
                finalize.Object);

            var pk = new RatchetEphemeralKey(new byte[] { 0xAA });
            var payload = SessionRatchetMessage.Create(pk, 1, 0, new Ciphertext(new byte[] { 0xBB })).Value;
            var cmd = new HandleHandshakeResponderHelloCommand(payload);

            // Act
            await handler.Handle(cmd, CancellationToken.None);

            // Assert
            lookup.Verify(l => l.TryResolveAsync(It.IsAny<RatchetEphemeralKey>(), It.IsAny<CancellationToken>()), Times.Once);
            lookup.VerifyAll();
        }
    }
}
