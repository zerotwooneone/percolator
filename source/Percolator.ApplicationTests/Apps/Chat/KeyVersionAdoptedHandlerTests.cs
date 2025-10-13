using System;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf.WellKnownTypes;
using MediatR;
using Moq;
using NUnit.Framework;
using Percolator.Application.Apps.Chat;
using Percolator.MessageQueue.Commands;
using Percolator.Network;
using Microsoft.Extensions.Logging.Abstractions;

namespace Percolator.ApplicationTests.Apps.Chat
{
    [TestFixture]
    public class KeyVersionAdoptedHandlerTests
    {
        private Mock<IMediator> _mediator = null!;
        private Mock<ISigningService> _signing = null!;
        private Mock<IRecipientPkhResolver> _pkh = null!;
        private Mock<IActingAdminResolver> _acting = null!;
        private Microsoft.Extensions.Logging.ILoggerFactory _loggerFactory = null!;
        private Percolator.Application.Identity.ActiveIdentityContext _active = null!;
        private Mock<Percolator.Application.Sessions.IDirectSessionManager> _sessions = null!;
        private Mock<IDirectSessionRepository> _directRepo = null!;

        [SetUp]
        public void SetUp()
        {
            _mediator = new Mock<IMediator>(MockBehavior.Strict);
            _signing = new Mock<ISigningService>(MockBehavior.Strict);
            _pkh = new Mock<IRecipientPkhResolver>(MockBehavior.Strict);
            _acting = new Mock<IActingAdminResolver>(MockBehavior.Strict);
            _loggerFactory = Microsoft.Extensions.Logging.LoggerFactory.Create(b=>{});
            _active = new Percolator.Application.Identity.ActiveIdentityContext { Identity = new Percolator.Identity.Model.IdentityRecord(Guid.NewGuid(), "self") { SelfIdentityId = 1 } };
            _sessions = new Mock<Percolator.Application.Sessions.IDirectSessionManager>(MockBehavior.Strict);
            _directRepo = new Mock<IDirectSessionRepository>(MockBehavior.Strict);
        }

        [TearDown]
        public void TearDown()
        {
            _loggerFactory?.Dispose();
        }

        private KeyVersionAdoptedHandler CreateSut()
        {
            var logger = NullLogger<KeyVersionAdoptedHandler>.Instance;
            return new KeyVersionAdoptedHandler(logger, _mediator.Object, _signing.Object, _active, _pkh.Object, _acting.Object, _sessions.Object, _directRepo.Object);
        }

        [Test]
        public async Task Sends_Confirmation_When_Admin_Pkh_Resolves()
        {
            var convoId = Guid.NewGuid();
            var keyVersion = 7u;
            var adminPeer = Guid.NewGuid();
            _acting.Setup(a => a.GetActingAdminPeerIdAsync(convoId, It.IsAny<CancellationToken>())).ReturnsAsync(adminPeer);

            var pub = new PublicKey(new byte[]{ 1,2,3 });
            _signing.Setup(s => s.GetActivePublicKey()).Returns(pub);
            var pkh = new byte[32];
            _pkh.Setup(p => p.GetActivePkhAsync(adminPeer, It.IsAny<CancellationToken>())).ReturnsAsync(pkh);
            _signing.Setup(s => s.Sign(It.IsAny<Payload>())).Returns(new Signature(new byte[]{ 9,9 }));

            // Session lookup + encrypt path
            var directId = new Percolator.Network.DirectSessionId(Guid.NewGuid());
            _directRepo.Setup(r => r.GetByRemotePeerIdAsync(It.IsAny<Percolator.Network.PeerId>(), It.IsAny<int>()))
                .ReturnsAsync(new Percolator.Network.DirectSession(new Percolator.Network.PeerId(adminPeer), directId));
            _sessions.Setup(s => s.EncryptMessageAsync(It.IsAny<Percolator.Cryptography.SessionId>(), It.IsAny<Percolator.Cryptography.Plaintext>()))
                .ReturnsAsync(Percolator.Cryptography.SessionRatchetMessage.Create(new Percolator.Cryptography.PreKey(new byte[]{1}), 0, 0, new Percolator.Cryptography.Ciphertext(new byte[]{2})));

            _mediator.Setup(m => m.Send(It.IsAny<EnqueueOpaqueMessageCommand>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new Percolator.MessageQueue.Results.EnqueueOpaqueMessageResult(true, null));
            var sut = CreateSut();
            await sut.Handle(new KeyVersionAdoptedNotification(convoId, keyVersion), CancellationToken.None);

            _mediator.Verify(m => m.Send(It.IsAny<EnqueueOpaqueMessageCommand>(), It.IsAny<CancellationToken>()), Times.Once);
        }
        [Test]
        public async Task Skips_When_Admin_Pkh_Missing()
        {
            var convoId = Guid.NewGuid();
            var keyVersion = 3u;
            var adminPeer = Guid.NewGuid();
            _acting.Setup(a => a.GetActingAdminPeerIdAsync(convoId, It.IsAny<CancellationToken>())).ReturnsAsync(adminPeer);

            var pub = new PublicKey(new byte[]{ 1,2,3 });
            _signing.Setup(s => s.GetActivePublicKey()).Returns(pub);
            _signing.Setup(s => s.Sign(It.IsAny<Payload>())).Returns(new Signature(new byte[]{ 9,9 }));

            _pkh.Setup(p => p.GetActivePkhAsync(adminPeer, It.IsAny<CancellationToken>())).ReturnsAsync((byte[]?)null);

            var sut = CreateSut();
            await sut.Handle(new KeyVersionAdoptedNotification(convoId, keyVersion), CancellationToken.None);

            _mediator.Verify(m => m.Send(It.IsAny<EnqueueOpaqueMessageCommand>(), It.IsAny<CancellationToken>()), Times.Never);
        }
    }
}
