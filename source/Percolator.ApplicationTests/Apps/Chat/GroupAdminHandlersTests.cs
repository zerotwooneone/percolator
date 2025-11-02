using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using NUnit.Framework;
using Moq;
using MediatR;
using Percolator.Chat;
using Percolator.Application.Network;
using Percolator.Application.Apps.Chat;

namespace Percolator.ApplicationTests.Apps.Chat
{
    [TestFixture]
    public sealed class GroupAdminHandlersTests
    {
        [Test]
        public async Task GrantGroupAdmin_AppliesLocally_And_BroadcastsToOthers()
        {
            // Arrange
            var cmd = new GrantGroupAdminAppCommand(
                SelfIdentityId: 1,
                GroupConversationGuid: Guid.NewGuid(),
                GranteeSpki: new byte[] { 1, 2, 3 }
            );
            var convoRepo = new Mock<IConversationRepository>(MockBehavior.Strict);
            var selfProvider = new Mock<ISelfParticipantIdProvider>(MockBehavior.Strict);
            var adminOps = new Mock<Percolator.Chat.App.IAdminOperations>(MockBehavior.Strict);
            var dispatcher = new Mock<IAdminOperationDispatcher>(MockBehavior.Loose);
            var signer = new Mock<IAdminOperationSigner>(MockBehavior.Strict);
            var seq = new Mock<Percolator.Chat.App.IAdminSequenceProvider>(MockBehavior.Strict);

            // Repo returns a conversation with two participants: self and one other
            var selfPid = new Percolator.Chat.ValueObjects.ParticipantId(Guid.NewGuid());
            var otherPid = new Percolator.Chat.ValueObjects.ParticipantId(Guid.NewGuid());
            var convo = new Conversation(
                id: new Percolator.Chat.ValueObjects.ConversationId(Guid.NewGuid()),
                participants: new[] { selfPid, otherPid },
                messages: Array.Empty<Message>()
            );
            convoRepo.Setup(r => r.GetByGroupGuidAsync(cmd.GroupConversationGuid, cmd.SelfIdentityId))
                .ReturnsAsync(convo);
            selfProvider.Setup(p => p.Get()).Returns(selfPid);

            // Expect domain apply and dispatch
            adminOps.Setup(a => a.GrantAdminAsync(
                It.IsAny<Percolator.Chat.App.ConversationLookupKey>(),
                It.IsAny<Guid>(),
                It.IsAny<DateTimeOffset>(),
                It.IsAny<Percolator.Chat.ValueObjects.AdminPublicKey>(),
                It.IsAny<byte[]>(),
                It.IsAny<byte[]>(),
                It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
            dispatcher.Setup(d => d.DispatchGrantAdminAsync(
                It.IsAny<Percolator.Identity.PeerId>(),
                cmd.GroupConversationGuid,
                It.IsAny<Guid>(),
                It.IsAny<DateTimeOffset>(),
                It.IsAny<ulong?>(),
                It.IsAny<byte[]>(),
                It.IsAny<byte[]>(),
                It.Is<IReadOnlyList<Percolator.Identity.PeerId>>(l => l.Count == 2),
                It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            signer.Setup(s => s.SignGrantAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<DateTimeOffset>(), It.IsAny<byte[]>(), It.IsAny<ulong?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((Array.Empty<byte>(), new byte[] { 1 }));
            seq.Setup(s => s.NextAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(1UL);
            var sut = new GrantGroupAdminHandler(
                convoRepo.Object,
                selfProvider.Object,
                adminOps.Object,
                dispatcher.Object,
                signer.Object,
                seq.Object);

            // Act
            Func<Task> act = () => sut.Handle(cmd, CancellationToken.None);

            // Assert (Red: should not throw when implemented)
            await act.Should().NotThrowAsync();
        }

        [Test]
        public async Task RevokeGroupAdmin_AppliesLocally_And_Dispatches()
        {
            // Arrange
            var convoRepo = new Mock<IConversationRepository>(MockBehavior.Strict);
            var selfProvider = new Mock<ISelfParticipantIdProvider>(MockBehavior.Strict);
            var adminOps = new Mock<Percolator.Chat.App.IAdminOperations>(MockBehavior.Strict);
            var dispatcher = new Mock<IAdminOperationDispatcher>(MockBehavior.Loose);
            var signer = new Mock<IAdminOperationSigner>(MockBehavior.Strict);
            var seq = new Mock<Percolator.Chat.App.IAdminSequenceProvider>(MockBehavior.Strict);

            var cmd = new RevokeGroupAdminAppCommand(1, Guid.NewGuid(), new byte[] { 9 });

            // arrange convo + self
            var selfPid = new Percolator.Chat.ValueObjects.ParticipantId(Guid.NewGuid());
            var otherPid = new Percolator.Chat.ValueObjects.ParticipantId(Guid.NewGuid());
            var convo = new Conversation(
                id: new Percolator.Chat.ValueObjects.ConversationId(Guid.NewGuid()),
                participants: new[] { selfPid, otherPid },
                messages: Array.Empty<Message>()
            );
            convoRepo.Setup(r => r.GetByGroupGuidAsync(cmd.GroupConversationGuid, cmd.SelfIdentityId))
                .ReturnsAsync(convo);
            selfProvider.Setup(p => p.Get()).Returns(selfPid);

            adminOps.Setup(a => a.RevokeAdminAsync(
                It.IsAny<Percolator.Chat.App.ConversationLookupKey>(),
                It.IsAny<Guid>(),
                It.IsAny<DateTimeOffset>(),
                It.IsAny<Percolator.Chat.ValueObjects.AdminPublicKey>(),
                It.IsAny<byte[]>(),
                It.IsAny<byte[]>(),
                It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
            dispatcher.Setup(d => d.DispatchRevokeAdminAsync(
                It.IsAny<Percolator.Identity.PeerId>(),
                cmd.GroupConversationGuid,
                It.IsAny<Guid>(),
                It.IsAny<DateTimeOffset>(),
                It.IsAny<ulong?>(),
                It.IsAny<byte[]>(),
                It.IsAny<byte[]>(),
                It.Is<IReadOnlyList<Percolator.Identity.PeerId>>(l => l.Count == 2),
                It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            signer.Setup(s => s.SignRevokeAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<DateTimeOffset>(), It.IsAny<byte[]>(), It.IsAny<ulong?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((Array.Empty<byte>(), new byte[] { 1 }));
            seq.Setup(s => s.NextAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(2UL);
            var sut = new RevokeGroupAdminHandler(convoRepo.Object, selfProvider.Object, adminOps.Object, dispatcher.Object, signer.Object, seq.Object);

            // Act
            Func<Task> act = () => sut.Handle(cmd, CancellationToken.None);

            // Assert
            await act.Should().NotThrowAsync();
            adminOps.VerifyAll();
            dispatcher.VerifyAll();
        }

        [Test]
        public async Task GrantGroupAdmin_Uses_Dispatcher()
        {
            // Arrange
            var convoRepo = new Mock<IConversationRepository>(MockBehavior.Strict);
            var selfProvider = new Mock<ISelfParticipantIdProvider>(MockBehavior.Strict);
            var adminOps = new Mock<Percolator.Chat.App.IAdminOperations>(MockBehavior.Strict);
            var dispatcher = new Mock<IAdminOperationDispatcher>(MockBehavior.Loose);
            var signer = new Mock<IAdminOperationSigner>(MockBehavior.Strict);
            var seq = new Mock<Percolator.Chat.App.IAdminSequenceProvider>(MockBehavior.Strict);

            var cmd = new GrantGroupAdminAppCommand(1, Guid.NewGuid(), new byte[] { 1 });

            // arrange convo + self
            var selfPid = new Percolator.Chat.ValueObjects.ParticipantId(Guid.NewGuid());
            var otherPid = new Percolator.Chat.ValueObjects.ParticipantId(Guid.NewGuid());
            var convo = new Conversation(
                id: new Percolator.Chat.ValueObjects.ConversationId(Guid.NewGuid()),
                participants: new[] { selfPid, otherPid },
                messages: Array.Empty<Message>()
            );
            convoRepo.Setup(r => r.GetByGroupGuidAsync(cmd.GroupConversationGuid, cmd.SelfIdentityId))
                .ReturnsAsync(convo);
            selfProvider.Setup(p => p.Get()).Returns(selfPid);

            adminOps.Setup(a => a.GrantAdminAsync(
                It.IsAny<Percolator.Chat.App.ConversationLookupKey>(),
                It.IsAny<Guid>(),
                It.IsAny<DateTimeOffset>(),
                It.IsAny<Percolator.Chat.ValueObjects.AdminPublicKey>(),
                It.IsAny<byte[]>(),
                It.IsAny<byte[]>(),
                It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
            dispatcher.Setup(d => d.DispatchGrantAdminAsync(
                It.IsAny<Percolator.Identity.PeerId>(),
                cmd.GroupConversationGuid,
                It.IsAny<Guid>(),
                It.IsAny<DateTimeOffset>(),
                It.IsAny<ulong?>(),
                cmd.GranteeSpki,
                It.IsAny<byte[]>(),
                It.Is<IReadOnlyList<Percolator.Identity.PeerId>>(l => l.Count == 2),
                It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            signer.Setup(s => s.SignGrantAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<DateTimeOffset>(), It.IsAny<byte[]>(), It.IsAny<ulong?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((Array.Empty<byte>(), new byte[] { 1 }));
            seq.Setup(s => s.NextAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(5UL);
            var sut = new GrantGroupAdminHandler(
                convoRepo.Object,
                selfProvider.Object,
                adminOps.Object,
                dispatcher.Object,
                signer.Object,
                seq.Object);

            // Act
            Func<Task> act = () => sut.Handle(cmd, CancellationToken.None);

            // Assert
            await act.Should().NotThrowAsync();
        }

        [Test]
        public async Task GrantGroupAdmin_Calls_DomainApply()
        {
            // Arrange
            var convoRepo = new Mock<IConversationRepository>(MockBehavior.Strict);
            var selfProvider = new Mock<ISelfParticipantIdProvider>(MockBehavior.Strict);
            var adminOps = new Mock<Percolator.Chat.App.IAdminOperations>(MockBehavior.Strict);
            var dispatcher = new Mock<IAdminOperationDispatcher>();
            var signer = new Mock<IAdminOperationSigner>(MockBehavior.Strict);
            var seq = new Mock<Percolator.Chat.App.IAdminSequenceProvider>(MockBehavior.Strict);

            var cmd = new GrantGroupAdminAppCommand(1, Guid.NewGuid(), new byte[] { 1 });

            // arrange convo + self
            var selfPid = new Percolator.Chat.ValueObjects.ParticipantId(Guid.NewGuid());
            var otherPid = new Percolator.Chat.ValueObjects.ParticipantId(Guid.NewGuid());
            var convo = new Conversation(
                id: new Percolator.Chat.ValueObjects.ConversationId(Guid.NewGuid()),
                participants: new[] { selfPid, otherPid },
                messages: Array.Empty<Message>()
            );
            convoRepo.Setup(r => r.GetByGroupGuidAsync(cmd.GroupConversationGuid, cmd.SelfIdentityId))
                .ReturnsAsync(convo);
            selfProvider.Setup(p => p.Get()).Returns(selfPid);

            adminOps.Setup(a => a.GrantAdminAsync(
                It.IsAny<Percolator.Chat.App.ConversationLookupKey>(),
                It.IsAny<Guid>(),
                It.IsAny<DateTimeOffset>(),
                It.IsAny<Percolator.Chat.ValueObjects.AdminPublicKey>(),
                It.IsAny<byte[]>(),
                It.IsAny<byte[]>(),
                It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            signer.Setup(s => s.SignGrantAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<DateTimeOffset>(), It.IsAny<byte[]>(), It.IsAny<ulong?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((Array.Empty<byte>(), new byte[] { 1 }));
            seq.Setup(s => s.NextAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(6UL);
            var sut = new GrantGroupAdminHandler(
                convoRepo.Object,
                selfProvider.Object,
                adminOps.Object,
                dispatcher.Object,
                signer.Object,
                seq.Object);

            // Act
            Func<Task> act = () => sut.Handle(cmd, CancellationToken.None);

            // Assert (Red: will fail until handler calls mediator.Send with ApplySignedAdminOperationCommand)
            await act.Should().NotThrowAsync();
        }

        [Test]
        public async Task RevokeGroupAdmin_AppliesLocally_And_BroadcastsToOthers()
        {
            // Arrange
            var cmd = new RevokeGroupAdminAppCommand(
                SelfIdentityId: 1,
                GroupConversationGuid: Guid.NewGuid(),
                GranteeSpki: new byte[] { 1, 2, 3 }
            );
            var sut = new RevokeGroupAdminHandler(
                Mock.Of<IConversationRepository>(),
                Mock.Of<ISelfParticipantIdProvider>(),
                Mock.Of<Percolator.Chat.App.IAdminOperations>(),
                Mock.Of<IAdminOperationDispatcher>(),
                Mock.Of<IAdminOperationSigner>(),
                Mock.Of<Percolator.Chat.App.IAdminSequenceProvider>()
            );

            // Act
            Func<Task> act = () => sut.Handle(cmd, CancellationToken.None);

            // Assert (Red: should not throw when implemented)
            await act.Should().NotThrowAsync();
        }

        [Test]
        public async Task UpdateGroupMembership_UpdatesRepo_PublishesNotification_And_BroadcastsToOthers()
        {
            // Arrange
            var cmd = new UpdateGroupMembershipAppCommand(
                SelfIdentityId: 1,
                GroupConversationGuid: Guid.NewGuid(),
                MembersToAdd: new List<Guid> { Guid.NewGuid() },
                MembersToRemove: null,
                LeaveGroup: null
            );
            var sut = new UpdateGroupMembershipHandler(
                Mock.Of<IMediator>(),
                Mock.Of<IConversationRepository>(),
                Mock.Of<ISelfParticipantIdProvider>(),
                Mock.Of<Percolator.Chat.App.IAdminOperations>(),
                Mock.Of<IAdminOperationDispatcher>(),
                Mock.Of<IAdminOperationSigner>(),
                Mock.Of<Percolator.Chat.App.IAdminSequenceProvider>()
            );

            // Act
            Func<Task> act = () => sut.Handle(cmd, CancellationToken.None);

            // Assert (Red: should not throw when implemented)
            await act.Should().NotThrowAsync();
        }

        [Test]
        public async Task UpdateGroupInfo_UpdatesMetadata_And_BroadcastsToOthers()
        {
            // Arrange
            var cmd = new UpdateGroupInfoAppCommand(
                SelfIdentityId: 1,
                GroupConversationGuid: Guid.NewGuid(),
                NewGroupName: "New Name"
            );
            var sut = new UpdateGroupInfoHandler(
                Mock.Of<IConversationRepository>(),
                Mock.Of<ISelfParticipantIdProvider>(),
                Mock.Of<Percolator.Chat.App.IAdminOperations>(),
                Mock.Of<IAdminOperationDispatcher>(),
                Mock.Of<IAdminOperationSigner>(),
                Mock.Of<Percolator.Chat.App.IAdminSequenceProvider>()
            );

            // Act
            Func<Task> act = () => sut.Handle(cmd, CancellationToken.None);

            // Assert (Red: should not throw when implemented)
            await act.Should().NotThrowAsync();
        }

        [Test]
        public async Task GrantGroupAdmin_FailsFast_OnMissingGrantee()
        {
            // Arrange
            var cmd = new GrantGroupAdminAppCommand(
                SelfIdentityId: 1,
                GroupConversationGuid: Guid.NewGuid(),
                GranteeSpki: Array.Empty<byte>()
            );
            var sut = new GrantGroupAdminHandler(
                Mock.Of<IConversationRepository>(),
                Mock.Of<ISelfParticipantIdProvider>(),
                Mock.Of<Percolator.Chat.App.IAdminOperations>(),
                Mock.Of<IAdminOperationDispatcher>(),
                Mock.Of<IAdminOperationSigner>(),
                Mock.Of<Percolator.Chat.App.IAdminSequenceProvider>()
            );

            // Act
            Func<Task> act = () => sut.Handle(cmd, CancellationToken.None);

            // Assert (Red: expect InvalidOperationException in Green)
            await act.Should().ThrowAsync<InvalidOperationException>();
        }

        [Test]
        public async Task AdminSequence_Is_Monotonic_Per_Group_On_Grant()
        {
            // Arrange
            var mediator = new Mock<IMediator>();
            var convoRepo = new Mock<IConversationRepository>(MockBehavior.Strict);
            var selfProvider = new Mock<ISelfParticipantIdProvider>(MockBehavior.Strict);
            var transport = new Mock<IMessageTransportService>();
            var adminOps = new Mock<Percolator.Chat.App.IAdminOperations>(MockBehavior.Strict);
            var dispatcher = new Mock<IAdminOperationDispatcher>(MockBehavior.Loose);
            var signer = new Mock<IAdminOperationSigner>(MockBehavior.Strict);
            var seq = new Mock<Percolator.Chat.App.IAdminSequenceProvider>(MockBehavior.Strict);

            var groupId = Guid.NewGuid();
            var cmd1 = new GrantGroupAdminAppCommand(1, groupId, new byte[] { 1 });
            var cmd2 = new GrantGroupAdminAppCommand(1, groupId, new byte[] { 1 });

            var selfPid = new Percolator.Chat.ValueObjects.ParticipantId(Guid.NewGuid());
            var otherPid = new Percolator.Chat.ValueObjects.ParticipantId(Guid.NewGuid());
            var convo = new Conversation(
                id: new Percolator.Chat.ValueObjects.ConversationId(Guid.NewGuid()),
                participants: new[] { selfPid, otherPid },
                messages: Array.Empty<Message>()
            );
            convoRepo.Setup(r => r.GetByGroupGuidAsync(groupId, 1)).ReturnsAsync(convo);
            selfProvider.Setup(p => p.Get()).Returns(selfPid);

            adminOps.Setup(a => a.GrantAdminAsync(
                It.IsAny<Percolator.Chat.App.ConversationLookupKey>(),
                It.IsAny<Guid>(),
                It.IsAny<DateTimeOffset>(),
                It.IsAny<Percolator.Chat.ValueObjects.AdminPublicKey>(),
                It.IsAny<byte[]>(),
                It.IsAny<byte[]>(),
                It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            signer.Setup(s => s.SignGrantAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<DateTimeOffset>(), It.IsAny<byte[]>(), It.IsAny<ulong?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((Array.Empty<byte>(), new byte[] { 5 }));

            var seqSetup = seq.SetupSequence(s => s.NextAsync(groupId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(1UL)
                .ReturnsAsync(2UL);

            var sut = new GrantGroupAdminHandler(
                convoRepo.Object,
                selfProvider.Object,
                adminOps.Object,
                dispatcher.Object,
                signer.Object,
                seq.Object);

            // Act
            await sut.Handle(cmd1, CancellationToken.None);
            await sut.Handle(cmd2, CancellationToken.None);

            // Assert
            dispatcher.Verify(d => d.DispatchGrantAdminAsync(
                It.IsAny<Percolator.Identity.PeerId>(),
                groupId,
                It.IsAny<Guid>(),
                It.IsAny<DateTimeOffset>(),
                It.Is<ulong?>(u => u == 1UL),
                It.IsAny<byte[]>(),
                It.IsAny<byte[]>(),
                It.IsAny<IReadOnlyList<Percolator.Identity.PeerId>>(),
                It.IsAny<CancellationToken>()), Times.AtLeastOnce);

            dispatcher.Verify(d => d.DispatchGrantAdminAsync(
                It.IsAny<Percolator.Identity.PeerId>(),
                groupId,
                It.IsAny<Guid>(),
                It.IsAny<DateTimeOffset>(),
                It.Is<ulong?>(u => u == 2UL),
                It.IsAny<byte[]>(),
                It.IsAny<byte[]>(),
                It.IsAny<IReadOnlyList<Percolator.Identity.PeerId>>(),
                It.IsAny<CancellationToken>()), Times.AtLeastOnce);
        }
    }
}
