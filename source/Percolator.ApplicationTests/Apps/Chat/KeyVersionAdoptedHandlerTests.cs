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

        [SetUp]
        public void SetUp()
        {
            _mediator = new Mock<IMediator>(MockBehavior.Strict);
            _signing = new Mock<ISigningService>(MockBehavior.Strict);
            _pkh = new Mock<IRecipientPkhResolver>(MockBehavior.Strict);
            _acting = new Mock<IActingAdminResolver>(MockBehavior.Strict);
            _loggerFactory = Microsoft.Extensions.Logging.LoggerFactory.Create(b=>{});
        }

        [TearDown]
        public void TearDown()
        {
            _loggerFactory?.Dispose();
        }

        private KeyVersionAdoptedHandler CreateSut()
        {
            var logger = NullLogger<KeyVersionAdoptedHandler>.Instance;
            return new KeyVersionAdoptedHandler(logger, _mediator.Object, _signing.Object, new Percolator.Application.Identity.ActiveIdentityContext(), _pkh.Object, _acting.Object);
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
            _signing.Setup(s => s.Sign(It.IsAny<Payload>())).Returns(new Signature(new byte[]{ 9,9 }));

            var pkh = new byte[32];
            _pkh.Setup(p => p.GetActivePkhAsync(adminPeer, It.IsAny<CancellationToken>())).ReturnsAsync(pkh);

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
