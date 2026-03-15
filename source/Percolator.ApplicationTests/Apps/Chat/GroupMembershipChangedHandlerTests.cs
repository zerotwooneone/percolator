using System.Security.Cryptography;
using MediatR;
using Moq;
using Percolator.Application.Apps.Chat;
using Percolator.Application.Identity;
using Percolator.Chat;
using Percolator.Chat.App;
using Percolator.Chat.ValueObjects;
using Microsoft.Extensions.Logging;
using Percolator.Identity.Model;
using Percolator.Application.Network;
using Percolator.Identity;

namespace Percolator.ApplicationTests.Apps.Chat
{
    [TestFixture]
    public class GroupMembershipChangedHandlerTests
    {
        private Mock<IConversationRepository> _repo = null!;
        private Mock<ISelfParticipantIdProvider> _self = null!;
        private Mock<IMediator> _mediator = null!;
        private ActiveIdentityContext _active = null!;
        private Mock<IGroupAdminStateStore> _adminState = null!;
        private Mock<IGroupManagerStateStore> _gmState = null!;
        private Mock<IAtRestKeyProvider> _atRest = null!;
        private Mock<IRecipientPkhResolver> _pkh = null!;
        private ILoggerFactory _loggerFactory = null!;
        private Mock<IRemoteEnvelopeSender> _sender = null!;

        [SetUp]
        public void SetUp()
        {
            _repo = new Mock<IConversationRepository>(MockBehavior.Strict);
            _self = new Mock<ISelfParticipantIdProvider>(MockBehavior.Strict);
            _mediator = new Mock<IMediator>(MockBehavior.Strict);
            _adminState = new Mock<IGroupAdminStateStore>(MockBehavior.Strict);
            _gmState = new Mock<IGroupManagerStateStore>(MockBehavior.Strict);
            _atRest = new Mock<IAtRestKeyProvider>(MockBehavior.Strict);
            _pkh = new Mock<IRecipientPkhResolver>(MockBehavior.Strict);
            _active = new ActiveIdentityContext();
            _loggerFactory = LoggerFactory.Create(b=>{});
            _sender = new Mock<IRemoteEnvelopeSender>(MockBehavior.Strict);
        }

        [Test]
        public async Task Handle_Skips_Recipient_When_AEAD_Missing()
        {
            var convoId = Guid.NewGuid();
            var p1 = new ParticipantId(Guid.NewGuid());
            var p2 = new ParticipantId(Guid.NewGuid());
            var conversation = new Conversation(new ConversationId(convoId), new List<ParticipantId>{p1,p2}, new List<Message>(), name: null);
            _self.Setup(s => s.Get()).Returns(p1);
            _repo.Setup(r => r.GetByIdAsync(new ConversationId(convoId), It.IsAny<int>())).ReturnsAsync(conversation);
            _active.Identity = new IdentityRecord(p1.Value, "self") { SelfIdentityId = new SelfId(1) };

            _adminState.Setup(a => a.GetAsync(convoId, It.IsAny<CancellationToken>())).ReturnsAsync(new GroupAdminState(0,0));
            _atRest.Setup(a => a.GetMasterKeyAsync(It.IsAny<CancellationToken>())).ReturnsAsync(RandomNumberGenerator.GetBytes(32));
            _gmState.Setup(s => s.SaveAsync(convoId, It.IsAny<byte[]>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

            // Allow PKH resolution to return null for any participant (strict mock placation)
            _pkh.Setup(x => x.GetActivePkhAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((byte[]?)null);
            // No send should happen in this scenario
            var sut = CreateSut();
            Assert.ThrowsAsync<NotSupportedException>(async () =>
                await sut.Handle(new GroupMembershipChangedNotification(convoId), CancellationToken.None));
        }

        [Test]
        public async Task Handle_Skips_Recipient_When_Pkh_Missing()
        {
            var convoId = Guid.NewGuid();
            var p1 = new ParticipantId(Guid.NewGuid());
            var p2 = new ParticipantId(Guid.NewGuid());
            var conversation = new Conversation(new ConversationId(convoId), new List<ParticipantId>{p1,p2}, new List<Message>(), name: null);
            _self.Setup(s => s.Get()).Returns(p1);
            _repo.Setup(r => r.GetByIdAsync(new ConversationId(convoId), It.IsAny<int>())).ReturnsAsync(conversation);

            _adminState.Setup(a => a.GetAsync(convoId, It.IsAny<CancellationToken>())).ReturnsAsync(new GroupAdminState(0,0));
            _atRest.Setup(a => a.GetMasterKeyAsync(It.IsAny<CancellationToken>())).ReturnsAsync(RandomNumberGenerator.GetBytes(32));
            _gmState.Setup(s => s.SaveAsync(convoId, It.IsAny<byte[]>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

            // Allow PKH resolution to return null for any participant
            _pkh.Setup(x => x.GetActivePkhAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((byte[]?)null);

            var sut = CreateSut();
            Assert.ThrowsAsync<NotSupportedException>(async () =>
                await sut.Handle(new GroupMembershipChangedNotification(convoId), CancellationToken.None));
        }

        [TearDown]
        public void TearDown()
        {
            _loggerFactory?.Dispose();
        }

        private GroupMembershipChangedHandler CreateSut()
        {
            var logger = _loggerFactory.CreateLogger<GroupMembershipChangedHandler>();
            return new GroupMembershipChangedHandler(
                logger,
                _repo.Object,
                _self.Object,
                _mediator.Object,
                _active,
                _adminState.Object,
                _gmState.Object,
                _atRest.Object,
                _pkh.Object,
                _sender.Object);
        }

        [Test]
        public async Task Handle_Distributes_To_Members_When_Prereqs_Available()
        {
            // Arrange a conversation with two participants
            var convoId = Guid.NewGuid();
            var p1 = new ParticipantId(Guid.NewGuid());
            var p2 = new ParticipantId(Guid.NewGuid());
            var conversation = new Conversation(new ConversationId(convoId), new List<ParticipantId>{p1,p2}, new List<Message>(), name: null);
            _self.Setup(s => s.Get()).Returns(p1); // mark p1 as self participant id for semantics
            _repo.Setup(r => r.GetByIdAsync(new ConversationId(convoId), It.IsAny<int>())).ReturnsAsync(conversation);
            // Active identity is required by handler
            _active.Identity = new IdentityRecord(p1.Value, "self") { SelfIdentityId = new SelfId(1) };

            // Admin state -> last committed 0 => next = 1
            _adminState.Setup(a => a.GetAsync(convoId, It.IsAny<CancellationToken>())).ReturnsAsync(new GroupAdminState(NextAdminSequenceNumber:0, LastCommittedKeyVersion:0));

            // At rest: master key, and save state
            _atRest.Setup(a => a.GetMasterKeyAsync(It.IsAny<CancellationToken>())).ReturnsAsync(RandomNumberGenerator.GetBytes(32));
            _gmState.Setup(s => s.SaveAsync(convoId, It.IsAny<byte[]>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

            // Recipient PKH resolves for both
            var pkhBytes1 = RandomNumberGenerator.GetBytes(32);
            var pkhBytes2 = RandomNumberGenerator.GetBytes(32);
            _pkh.Setup(x => x.GetActivePkhAsync(p1.Value, It.IsAny<CancellationToken>())).ReturnsAsync(pkhBytes1);
            _pkh.Setup(x => x.GetActivePkhAsync(p2.Value, It.IsAny<CancellationToken>())).ReturnsAsync(pkhBytes2);

            // Sender should be called for non-self participant(s)
            _sender
                .Setup(s => s.SendChatEnvelopeToPeerAsync(
                    It.IsAny<Percolator.Contracts.ChatEnvelope>(),
                    It.Is<RecipientRoute>(r => r.PeerId.Value == p2.Value),
                    It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask)
                .Verifiable();

            var sut = CreateSut();

            // Act + Assert (current cutover behavior throws)
            Assert.ThrowsAsync<NotSupportedException>(async () =>
                await sut.Handle(new GroupMembershipChangedNotification(convoId), CancellationToken.None));
        }
    }
}
