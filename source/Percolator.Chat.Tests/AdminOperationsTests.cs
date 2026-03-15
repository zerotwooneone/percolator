using FluentAssertions;
using Moq;
using Percolator.Chat.App;
using Percolator.Chat.App.Services;
using Percolator.Chat.ValueObjects;

namespace Percolator.Chat.Tests
{
    [TestFixture]
    public class AdminOperationsTests
    {
        private static (AdminOperations sut,
            Mock<IConversationResolver> resolver,
            Mock<IConversationRepository> repo,
            Mock<ISelfParticipantIdProvider> self,
            Mock<IGroupAdminKeyStore> keyStore,
            Mock<IGroupAdminOpStore> opStore,
            Mock<IAdminSignatureVerifier> verifier,
            Mock<MediatR.IMediator> mediator,
            Conversation convo,
            int selfIdentityId)
            CreateSut()
        {
            var resolver = new Mock<IConversationResolver>(MockBehavior.Strict);
            var repo = new Mock<IConversationRepository>(MockBehavior.Loose);
            var self = new Mock<ISelfParticipantIdProvider>(MockBehavior.Loose);
            var keyStore = new Mock<IGroupAdminKeyStore>(MockBehavior.Strict);
            var opStore = new Mock<IGroupAdminOpStore>(MockBehavior.Strict);
            var verifier = new Mock<IAdminSignatureVerifier>(MockBehavior.Strict);
            var mediator = new Mock<MediatR.IMediator>(MockBehavior.Loose);

            var convoId = new ConversationId(Guid.NewGuid());
            var pSelf = new ParticipantId(Guid.NewGuid());
            var pOther = new ParticipantId(Guid.NewGuid());
            var convo = new ConversationBuilder()
                .WithId(convoId)
                .WithParticipants(pSelf, pOther)
                .Build();
            var selfIdentityId = 42;

            resolver.Setup(r => r.ResolveAsync(It.IsAny<ConversationLookupKey>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((ConversationLookupKey key, CancellationToken _) => new ConversationResolution(convo, selfIdentityId));
            self.Setup(s => s.Get()).Returns(pSelf);

            var sut = new AdminOperations(resolver.Object, repo.Object, self.Object, keyStore.Object, opStore.Object, verifier.Object, mediator.Object);
            return (sut, resolver, repo, self, keyStore, opStore, verifier, mediator, convo, selfIdentityId);
        }

        [Test]
        public async Task GrantAdmin_Applies_When_SignatureValid_And_KeyValidAtTime()
        {
            var (sut, _, _, _, keyStore, opStore, verifier, _, convo, _) = CreateSut();
            var lookup = ConversationLookupKey.ForGroup(Guid.NewGuid());
            var opId = Guid.NewGuid();
            var sent = DateTimeOffset.UtcNow;
            var grantee = new AdminPublicKey(new byte[] { 7 });
            var signature = new byte[] { 9 };
            var payload = new byte[] { 1, 2 };

            opStore.Setup(s => s.TryAddAsync(convo.Id.Value, opId, sent, It.IsAny<CancellationToken>())).ReturnsAsync(true);
            keyStore.Setup(s => s.GetKeysAsync(convo.Id.Value, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<GroupAdminKeyRecord> { new GroupAdminKeyRecord(grantee, sent.AddMinutes(-1), null) });
            verifier.Setup(v => v.VerifyAsync(grantee, payload, signature, It.IsAny<CancellationToken>())).ReturnsAsync(true);
            keyStore.Setup(s => s.AddKeyAsync(convo.Id.Value, grantee, sent, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

            await sut.GrantAdminAsync(lookup, opId, sent, grantee, signature, payload, CancellationToken.None);
        }

        [Test]
        public async Task GrantAdmin_Is_Idempotent_When_AlreadyApplied()
        {
            var (sut, _, _, _, keyStore, opStore, verifier, _, convo, _) = CreateSut();
            var lookup = ConversationLookupKey.ForGroup(Guid.NewGuid());
            var opId = Guid.NewGuid();
            var sent = DateTimeOffset.UtcNow;
            var grantee = new AdminPublicKey(new byte[] { 7 });
            var signature = new byte[] { 9 };
            var payload = new byte[] { 1, 2 };

            opStore.Setup(s => s.TryAddAsync(convo.Id.Value, opId, sent, It.IsAny<CancellationToken>())).ReturnsAsync(false);

            await sut.GrantAdminAsync(lookup, opId, sent, grantee, signature, payload, CancellationToken.None);

            verifier.Verify(v => v.VerifyAsync(It.IsAny<AdminPublicKey>(), It.IsAny<byte[]>(), It.IsAny<byte[]>(), It.IsAny<CancellationToken>()), Times.Never);
            keyStore.Verify(s => s.AddKeyAsync(It.IsAny<Guid>(), It.IsAny<AdminPublicKey>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Test]
        public async Task RevokeAdmin_Applies_When_SignatureValid()
        {
            var (sut, _, _, _, keyStore, opStore, verifier, _, convo, _) = CreateSut();
            var lookup = ConversationLookupKey.ForGroup(Guid.NewGuid());
            var opId = Guid.NewGuid();
            var sent = DateTimeOffset.UtcNow;
            var grantee = new AdminPublicKey(new byte[] { 7 });
            var signature = new byte[] { 9 };
            var payload = new byte[] { 1, 2 };

            opStore.Setup(s => s.TryAddAsync(convo.Id.Value, opId, sent, It.IsAny<CancellationToken>())).ReturnsAsync(true);
            keyStore.Setup(s => s.GetKeysAsync(convo.Id.Value, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<GroupAdminKeyRecord> { new GroupAdminKeyRecord(grantee, sent.AddMinutes(-1), null) });
            verifier.Setup(v => v.VerifyAsync(grantee, payload, signature, It.IsAny<CancellationToken>())).ReturnsAsync(true);
            keyStore.Setup(s => s.RevokeKeyAsync(convo.Id.Value, grantee, sent, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

            await sut.RevokeAdminAsync(lookup, opId, sent, grantee, signature, payload, CancellationToken.None);
        }

        [Test]
        public async Task UpdateGroupMembership_Adds_Removes_And_Publishes()
        {
            var (sut, _, repo, self, keyStore, opStore, verifier, mediator, convo, _) = CreateSut();
            var lookup = ConversationLookupKey.ForGroup(Guid.NewGuid());
            var opId = Guid.NewGuid();
            var sent = DateTimeOffset.UtcNow;
            var add = new ParticipantId(Guid.NewGuid());
            var remove = convo.Participants[1];
            var signature = new byte[] { 9 };
            var payload = new byte[] { 1, 2 };

            opStore.Setup(s => s.TryAddAsync(convo.Id.Value, opId, sent, It.IsAny<CancellationToken>())).ReturnsAsync(true);
            keyStore.Setup(s => s.GetKeysAsync(convo.Id.Value, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<GroupAdminKeyRecord> { new GroupAdminKeyRecord(new AdminPublicKey(new byte[]{7}), sent.AddMinutes(-1), null) });
            verifier.Setup(v => v.VerifyAsync(It.IsAny<AdminPublicKey>(), payload, signature, It.IsAny<CancellationToken>())).ReturnsAsync(true);
            repo.Setup(r => r.UpdateAsync(It.IsAny<Conversation>(), It.IsAny<int>())).Returns(Task.CompletedTask);
            opStore.Setup(s => s.SetActingAdminAsync(convo.Id.Value, opId, self.Object.Get().Value, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
            mediator.Setup(m => m.Publish(It.IsAny<GroupMembershipChangedNotification>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

            await sut.UpdateGroupMembershipAsync(lookup, opId, sent, new List<ParticipantId>{ add }, new List<ParticipantId>{ remove }, false, signature, payload, CancellationToken.None);

            convo.Participants.Should().Contain(add);
            convo.Participants.Should().NotContain(remove);
        }

        [Test]
        public async Task UpdateGroupInfo_Changes_Name()
        {
            var (sut, _, repo, _, keyStore, opStore, verifier, _, convo, _) = CreateSut();
            var lookup = ConversationLookupKey.ForGroup(Guid.NewGuid());
            var opId = Guid.NewGuid();
            var sent = DateTimeOffset.UtcNow;
            var signature = new byte[] { 9 };
            var payload = new byte[] { 1, 2 };

            opStore.Setup(s => s.TryAddAsync(convo.Id.Value, opId, sent, It.IsAny<CancellationToken>())).ReturnsAsync(true);
            keyStore.Setup(s => s.GetKeysAsync(convo.Id.Value, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<GroupAdminKeyRecord> { new GroupAdminKeyRecord(new AdminPublicKey(new byte[]{7}), sent.AddMinutes(-1), null) });
            verifier.Setup(v => v.VerifyAsync(It.IsAny<AdminPublicKey>(), payload, signature, It.IsAny<CancellationToken>())).ReturnsAsync(true);
            repo.Setup(r => r.UpdateAsync(It.IsAny<Conversation>(), It.IsAny<int>())).Returns(Task.CompletedTask);

            await sut.UpdateGroupInfoAsync(lookup, opId, sent, "NewName", null, signature, payload, CancellationToken.None);
            convo.Name.Should().Be("NewName");
        }

        [Test]
        public async Task Apply_Fails_When_No_Valid_Keys_At_Time()
        {
            var (sut, _, _, _, keyStore, opStore, verifier, _, convo, _) = CreateSut();
            var lookup = ConversationLookupKey.ForGroup(Guid.NewGuid());
            var opId = Guid.NewGuid();
            var sent = DateTimeOffset.UtcNow;
            var signature = new byte[] { 9 };
            var payload = new byte[] { 1, 2 };

            opStore.Setup(s => s.TryAddAsync(convo.Id.Value, opId, sent, It.IsAny<CancellationToken>())).ReturnsAsync(true);
            keyStore.Setup(s => s.GetKeysAsync(convo.Id.Value, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<GroupAdminKeyRecord>());

            Func<Task> act = () => sut.UpdateGroupInfoAsync(lookup, opId, sent, null, null, signature, payload, CancellationToken.None);
            await act.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("No admin keys were valid at the operation timestamp.");
        }

        [Test]
        public async Task Apply_Fails_When_Signature_Invalid()
        {
            var (sut, _, _, _, keyStore, opStore, verifier, _, convo, _) = CreateSut();
            var lookup = ConversationLookupKey.ForGroup(Guid.NewGuid());
            var opId = Guid.NewGuid();
            var sent = DateTimeOffset.UtcNow;
            var signature = new byte[] { 9 };
            var payload = new byte[] { 1, 2 };

            opStore.Setup(s => s.TryAddAsync(convo.Id.Value, opId, sent, It.IsAny<CancellationToken>())).ReturnsAsync(true);
            var adminKey = new AdminPublicKey(new byte[] { 7 });
            keyStore.Setup(s => s.GetKeysAsync(convo.Id.Value, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<GroupAdminKeyRecord> { new GroupAdminKeyRecord(adminKey, sent.AddMinutes(-1), null) });
            verifier.Setup(v => v.VerifyAsync(adminKey, payload, signature, It.IsAny<CancellationToken>())).ReturnsAsync(false);

            Func<Task> act = () => sut.UpdateGroupInfoAsync(lookup, opId, sent, null, null, signature, payload, CancellationToken.None);
            await act.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("Admin signature verification failed against historical key set.");
        }
    }
}
