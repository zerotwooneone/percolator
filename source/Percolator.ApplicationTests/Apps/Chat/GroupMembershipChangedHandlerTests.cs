using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using MediatR;
using Moq;
using NUnit.Framework;
using Percolator.Application.Apps.Chat;
using Percolator.Application.Identity;
using Percolator.Chat;
using Percolator.Chat.App;
using Percolator.Chat.ValueObjects;
using Percolator.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Percolator.MessageQueue.Commands;
using Percolator.Identity.Model;

namespace Percolator.ApplicationTests.Apps.Chat
{
    [TestFixture]
    public class GroupMembershipChangedHandlerTests
    {
        private Mock<IConversationRepository> _repo = null!;
        private Mock<ISelfParticipantIdProvider> _self = null!;
        private Mock<IGroupManagerResolver> _gmResolver = null!;
        private Mock<ITransportKeyResolver> _transport = null!;
        private Mock<IMediator> _mediator = null!;
        private ActiveIdentityContext _active = null!;
        private Mock<IGroupAdminStateStore> _adminState = null!;
        private Mock<IGroupManagerStateStore> _gmState = null!;
        private Mock<IAtRestKeyProvider> _atRest = null!;
        private Mock<IRecipientPkhResolver> _pkh = null!;
        private ILoggerFactory _loggerFactory = null!;

        [SetUp]
        public void SetUp()
        {
            _repo = new Mock<IConversationRepository>(MockBehavior.Strict);
            _self = new Mock<ISelfParticipantIdProvider>(MockBehavior.Strict);
            _gmResolver = new Mock<IGroupManagerResolver>(MockBehavior.Strict);
            _transport = new Mock<ITransportKeyResolver>(MockBehavior.Strict);
            _mediator = new Mock<IMediator>(MockBehavior.Strict);
            _adminState = new Mock<IGroupAdminStateStore>(MockBehavior.Strict);
            _gmState = new Mock<IGroupManagerStateStore>(MockBehavior.Strict);
            _atRest = new Mock<IAtRestKeyProvider>(MockBehavior.Strict);
            _pkh = new Mock<IRecipientPkhResolver>(MockBehavior.Strict);
            _active = new ActiveIdentityContext();
            _loggerFactory = LoggerFactory.Create(b=>{});
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
            _active.Identity = new IdentityRecord(p1.Value, "self") { SelfIdentityId = 1 };

            var gm = new GroupManager(ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256), _loggerFactory, Options.Create(new CryptographyOptions()));
            _gmResolver.Setup(r => r.TryGet(convoId, out gm)).Returns(true);

            _adminState.Setup(a => a.GetAsync(convoId, It.IsAny<CancellationToken>())).ReturnsAsync(new GroupAdminState(0,0));
            _atRest.Setup(a => a.GetMasterKeyAsync(It.IsAny<CancellationToken>())).ReturnsAsync(RandomNumberGenerator.GetBytes(32));
            _gmState.Setup(s => s.SaveAsync(convoId, It.IsAny<byte[]>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

            // AEAD missing for p2
            _transport.Setup(t => t.GetAeadKeyAsync(convoId, p2.Value, It.IsAny<CancellationToken>())).ReturnsAsync((byte[]?)null);

            // Allow PKH resolution to return null for any participant (strict mock placation)
            _pkh.Setup(x => x.GetActivePkhAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((byte[]?)null);
            // No enqueue should happen in this scenario
            var sut = CreateSut();
            await sut.Handle(new GroupMembershipChangedNotification(convoId), CancellationToken.None);

            _mediator.Verify(m => m.Send(It.IsAny<EnqueueOpaqueMessageCommand>(), It.IsAny<CancellationToken>()), Times.Never());
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

            var gm = new GroupManager(ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256), _loggerFactory, Options.Create(new CryptographyOptions()));
            _gmResolver.Setup(r => r.TryGet(convoId, out gm)).Returns(true);

            _adminState.Setup(a => a.GetAsync(convoId, It.IsAny<CancellationToken>())).ReturnsAsync(new GroupAdminState(0,0));
            _atRest.Setup(a => a.GetMasterKeyAsync(It.IsAny<CancellationToken>())).ReturnsAsync(RandomNumberGenerator.GetBytes(32));
            _gmState.Setup(s => s.SaveAsync(convoId, It.IsAny<byte[]>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

            // AEAD for any recipient (self is skipped; p2 will be processed)
            _transport.Setup(t => t.GetAeadKeyAsync(convoId, It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(RandomNumberGenerator.GetBytes(32));
            // Allow PKH resolution to return null for any participant
            _pkh.Setup(x => x.GetActivePkhAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((byte[]?)null);

            var sut = CreateSut();
            await sut.Handle(new GroupMembershipChangedNotification(convoId), CancellationToken.None);

            _mediator.Verify(m => m.Send(It.IsAny<EnqueueOpaqueMessageCommand>(), It.IsAny<CancellationToken>()), Times.Never());
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
                _gmResolver.Object,
                _transport.Object,
                _mediator.Object,
                _active,
                _adminState.Object,
                _gmState.Object,
                _atRest.Object,
                _pkh.Object);
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

            // GroupManager available
            var gm = new GroupManager(ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256), _loggerFactory, Options.Create(new CryptographyOptions()));
            _gmResolver.Setup(r => r.TryGet(convoId, out gm)).Returns(true);

            // Admin state -> last committed 0 => next = 1
            _adminState.Setup(a => a.GetAsync(convoId, It.IsAny<CancellationToken>())).ReturnsAsync(new GroupAdminState(NextAdminSequenceNumber:0, LastCommittedKeyVersion:0));

            // At rest: master key, and save state
            _atRest.Setup(a => a.GetMasterKeyAsync(It.IsAny<CancellationToken>())).ReturnsAsync(RandomNumberGenerator.GetBytes(32));
            _gmState.Setup(s => s.SaveAsync(convoId, It.IsAny<byte[]>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

            // Transport AEAD always available
            _transport.Setup(t => t.GetAeadKeyAsync(convoId, p1.Value, It.IsAny<CancellationToken>())).ReturnsAsync(RandomNumberGenerator.GetBytes(32));
            _transport.Setup(t => t.GetAeadKeyAsync(convoId, p2.Value, It.IsAny<CancellationToken>())).ReturnsAsync(RandomNumberGenerator.GetBytes(32));

            // Recipient PKH resolves for both
            var pkhBytes1 = RandomNumberGenerator.GetBytes(32);
            var pkhBytes2 = RandomNumberGenerator.GetBytes(32);
            _pkh.Setup(x => x.GetActivePkhAsync(p1.Value, It.IsAny<CancellationToken>())).ReturnsAsync(pkhBytes1);
            _pkh.Setup(x => x.GetActivePkhAsync(p2.Value, It.IsAny<CancellationToken>())).ReturnsAsync(pkhBytes2);

            // Mediator should enqueue twice
            _mediator
                .Setup(m => m.Send(It.IsAny<EnqueueOpaqueMessageCommand>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new Percolator.MessageQueue.Results.EnqueueOpaqueMessageResult(true, null));

            var sut = CreateSut();

            // Act
            await sut.Handle(new GroupMembershipChangedNotification(convoId), CancellationToken.None);

            // Assert
            _repo.VerifyAll();
            _gmResolver.VerifyAll();
            _adminState.VerifyAll();
            _gmState.VerifyAll();
            _pkh.VerifyAll();
            _mediator.Verify(m => m.Send(It.IsAny<EnqueueOpaqueMessageCommand>(), It.IsAny<CancellationToken>()), Times.AtLeastOnce());
        }
    }
}
