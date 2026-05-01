using MediatR;
using Moq;
using Percolator.Application.Apps.Chat;
using Percolator.Network;
using Microsoft.Extensions.Logging.Abstractions;
using Percolator.Application.Network;
using Percolator.Identity;

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
        private Mock<IRemoteEnvelopeSender> _sender = null!;

        [SetUp]
        public void SetUp()
        {
            _mediator = new Mock<IMediator>(MockBehavior.Strict);
            _signing = new Mock<ISigningService>(MockBehavior.Strict);
            _pkh = new Mock<IRecipientPkhResolver>(MockBehavior.Strict);
            _acting = new Mock<IActingAdminResolver>(MockBehavior.Strict);
            _loggerFactory = Microsoft.Extensions.Logging.LoggerFactory.Create(b=>{});
            _active = new Percolator.Application.Identity.ActiveIdentityContext { Identity = new Percolator.Identity.Model.IdentityRecord(Guid.NewGuid(), "self") { SelfIdentityId = new SelfId(1) } };
            _sender = new Mock<IRemoteEnvelopeSender>(MockBehavior.Strict);
        }

        [TearDown]
        public void TearDown()
        {
            _loggerFactory?.Dispose();
        }

        private KeyVersionAdoptedHandler CreateSut()
        {
            var logger = NullLogger<KeyVersionAdoptedHandler>.Instance;
            return new KeyVersionAdoptedHandler(logger, _mediator.Object, _signing.Object, _active, _pkh.Object, _acting.Object, _sender.Object);
        }

        [Test]
        public async Task Sends_Confirmation_When_Admin_Pkh_Resolves()
        {
            var convoId = Guid.NewGuid();
            var keyVersion = 7u;
            var adminPeer = Guid.NewGuid();
            _acting.Setup(a => a.GetActingAdminPeerIdAsync(convoId, It.IsAny<CancellationToken>())).ReturnsAsync(adminPeer);

            var pub = PublicKey.FromBytes(new byte[80]);
            _signing.Setup(s => s.GetActivePublicKey()).Returns(pub);
            var pkh = new byte[32];
            _pkh.Setup(p => p.GetActivePkhAsync(adminPeer, It.IsAny<CancellationToken>())).ReturnsAsync(pkh);
            _signing.Setup(s => s.Sign(It.IsAny<Payload>())).Returns(Signature.FromBytes(new byte[64]));

            _sender
                .Setup(s => s.SendChatEnvelopeToPeerAsync(
                    It.IsAny<Percolator.Contracts.ChatEnvelope>(),
                    It.Is<RecipientRoute>(r => r.PeerId.Value == adminPeer),
                    It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask)
                .Verifiable();
            var sut = CreateSut();
            await sut.Handle(new KeyVersionAdoptedNotification(convoId, keyVersion), CancellationToken.None);
            _sender.Verify();
        }
        [Test]
        public async Task Sends_Using_Fallback_When_Admin_Pkh_Missing()
        {
            var convoId = Guid.NewGuid();
            var keyVersion = 3u;
            var adminPeer = Guid.NewGuid();
            _acting.Setup(a => a.GetActingAdminPeerIdAsync(convoId, It.IsAny<CancellationToken>())).ReturnsAsync(adminPeer);

            var pub = PublicKey.FromBytes(new byte[80]);
            _signing.Setup(s => s.GetActivePublicKey()).Returns(pub);
            _signing.Setup(s => s.Sign(It.IsAny<Payload>())).Returns(Signature.FromBytes(new byte[64]));

            _pkh.Setup(p => p.GetActivePkhAsync(adminPeer, It.IsAny<CancellationToken>())).ReturnsAsync((byte[]?)null);

            _sender
                .Setup(s => s.SendChatEnvelopeToPeerAsync(
                    It.IsAny<Percolator.Contracts.ChatEnvelope>(),
                    It.Is<RecipientRoute>(r => r.PeerId.Value == adminPeer && r.PublicKeyHash == null),
                    It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask)
                .Verifiable();

            var sut = CreateSut();
            await sut.Handle(new KeyVersionAdoptedNotification(convoId, keyVersion), CancellationToken.None);
            _sender.Verify();
        }
    }
}
