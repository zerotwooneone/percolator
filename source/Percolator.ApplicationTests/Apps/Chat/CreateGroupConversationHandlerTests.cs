using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using NUnit.Framework;
using Moq;
using Percolator.Chat;
using Percolator.Chat.App;
using Percolator.Chat.ValueObjects;
using Percolator.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Percolator.Application.Apps.Chat;

namespace Percolator.ApplicationTests.Apps.Chat
{
    [TestFixture]
    public sealed class CreateGroupConversationHandlerTests
    {
        [Test]
        public async Task CreateGroupConversation_CreatesGroup_And_SeedsCreatorAdminKey()
        {
            // Arrange
            var groupGuid = Guid.NewGuid();
            var aliceSpki = new byte[] { 1, 2, 3 };
            var bobSpki = new byte[] { 4, 5, 6 };
            var charlieSpki = new byte[] { 7, 8, 9 };
            var creatorSpki = aliceSpki;

            var logger = NullLogger<CreateGroupConversationHandler>.Instance;
            var keyStore = new Mock<IPeerPublicSigningKeyStore>(MockBehavior.Strict);
            var repo = new Mock<IConversationRepository>(MockBehavior.Strict);
            var adminKeys = new Mock<IGroupAdminKeyStore>(MockBehavior.Strict);

            // PKH resolver for 3 SPKIs => 3 PeerIds
            static byte[] Sha256(byte[] x) { using var sha = System.Security.Cryptography.SHA256.Create(); return sha.ComputeHash(x); }
            var alicePkh = Sha256(aliceSpki);
            var bobPkh = Sha256(bobSpki);
            var charliePkh = Sha256(charlieSpki);

            var alicePeer = new PeerId(Guid.NewGuid());
            var bobPeer = new PeerId(Guid.NewGuid());
            var charliePeer = new PeerId(Guid.NewGuid());

            keyStore.Setup(s => s.GetPeerIdByPublicKeyHashAsync(It.Is<byte[]>(b => b.SequenceEqual(alicePkh)), It.IsAny<CancellationToken>()))
                .ReturnsAsync(alicePeer);
            keyStore.Setup(s => s.GetPeerIdByPublicKeyHashAsync(It.Is<byte[]>(b => b.SequenceEqual(bobPkh)), It.IsAny<CancellationToken>()))
                .ReturnsAsync(bobPeer);
            keyStore.Setup(s => s.GetPeerIdByPublicKeyHashAsync(It.Is<byte[]>(b => b.SequenceEqual(charliePkh)), It.IsAny<CancellationToken>()))
                .ReturnsAsync(charliePeer);

            // Expect CreateGroupAsync with participants containing the resolved PeerIds
            repo.Setup(r => r.CreateGroupAsync(
                groupGuid,
                1,
                It.Is<IEnumerable<ParticipantId>>(p => p.Select(x => x.Value).ToHashSet().SetEquals(new[] { alicePeer.Value, bobPeer.Value, charliePeer.Value })),
                "Test Group"))
                .Returns(Task.CompletedTask);

            // After create, GetByGroupGuidAsync returns a conversation with Id
            var conversationId = new ConversationId(Guid.NewGuid());
            repo.Setup(r => r.GetByGroupGuidAsync(groupGuid, 1))
                .ReturnsAsync(new Conversation(conversationId,
                    new[] { new ParticipantId(alicePeer.Value), new ParticipantId(bobPeer.Value) },
                    Array.Empty<Message>(),
                    "Test Group"));

            // Expect admin key seeded for creator
            adminKeys.Setup(a => a.AddKeyAsync(
                conversationId.Value,
                It.Is<AdminPublicKey>(k => k.Bytes.SequenceEqual(creatorSpki)),
                It.IsAny<DateTimeOffset>(),
                It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            var sut = new CreateGroupConversationHandler(logger, keyStore.Object, repo.Object, adminKeys.Object);

            var cmd = new CreateGroupConversationCommand(
                SelfIdentityId: 1,
                GroupConversationGuid: groupGuid,
                ParticipantIdentityKeysSpki: new List<byte[]> { aliceSpki, bobSpki, charlieSpki },
                Name: "Test Group",
                CreatorIdentityKeySpki: creatorSpki
            );

            // Act
            Func<Task> act = () => sut.Handle(cmd, CancellationToken.None);

            // Assert
            await act.Should().NotThrowAsync();
            repo.VerifyAll();
            adminKeys.VerifyAll();
        }

        [Test]
        public async Task CreateGroupConversation_FailsFast_OnEmptyParticipants()
        {
            // Arrange
            var cmd = new CreateGroupConversationCommand(
                SelfIdentityId: 1,
                GroupConversationGuid: Guid.NewGuid(),
                ParticipantIdentityKeysSpki: Array.Empty<byte[]>(),
                Name: null,
                CreatorIdentityKeySpki: new byte[] { 1 }
            );
            var sut = new CreateGroupConversationHandler(NullLogger<CreateGroupConversationHandler>.Instance, Mock.Of<IPeerPublicSigningKeyStore>(), Mock.Of<IConversationRepository>(), Mock.Of<IGroupAdminKeyStore>());

            // Act
            Func<Task> act = () => sut.Handle(cmd, CancellationToken.None);

            // Assert
            await act.Should().ThrowAsync<InvalidOperationException>();
        }
    }
}
