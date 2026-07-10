using FluentAssertions;
using Moq;
using Percolator.Application.Chat;
using Percolator.Chat;
using Percolator.Chat.GroupLedger;
using Percolator.Chat.GroupMembership;
using Percolator.Chat.Messaging;
using Percolator.Chat.Messaging.App;
using Percolator.Chat.Messaging.ValueObjects;
using Percolator.Cryptography;
using Percolator.Cryptography.Primitives;
using Percolator.Identity;
using PublicIdentityId = Percolator.Identity.PublicIdentityId;
using ChatConversationId = Percolator.Chat.Messaging.ValueObjects.ConversationId;
using CryptoConversationId = Percolator.Cryptography.Primitives.ConversationId;
using CryptoDeviceId = Percolator.Cryptography.Primitives.DeviceId;
using ChatPublicIdentityId = Percolator.Chat.GroupLedger.PublicIdentityId;

namespace Percolator.ApplicationTests.Chat;

[TestFixture]
public class GroupStreamIngressProcessorTests
{
    private static readonly DateTimeOffset FixedTime = new DateTimeOffset(2025, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Test]
    public async Task ProcessGroupMessageAsync_DoesNotPersist_WhenSenderNotInGroup()
    {
        // ARRANGE
        var groupConversationRepository = new Mock<IGroupConversationRepository>();
        var peerIdentityQueries = new Mock<IPeerIdentityQueries>();
        var senderKeyCryptographyService = new Mock<ISenderKeyCryptographyService>();
        var chatMessageWriter = new Mock<IChatMessageWriter>();

        var conversationId = Guid.NewGuid();
        var senderPublicIdentityId = Guid.NewGuid();
        var senderPeerId = new PeerId(1);
        
        peerIdentityQueries.Setup(q => q.GetPeerIdByPublicIdentityIdAsync(
                It.IsAny<PublicIdentityId>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(senderPeerId);

        var groupConversation = new GroupConversation(
            new ChatConversationId(conversationId),
            new GroupState(new ChatConversationId(conversationId), 1, null, RelayGroupPublicParamsBytes.FromBytes(new byte[32]), FixedTime, FixedTime),
            new ChatPeerId(999),
            members: new List<GroupMember>
            {
                new GroupMember(new ChatConversationId(conversationId), new RemoteParticipantId(new ChatPublicIdentityId(Guid.NewGuid()), new ChatPeerId(2)), GroupMemberRole.Member, FixedTime)
            });

        groupConversationRepository.Setup(r => r.GetByIdAsync(
                It.IsAny<ChatConversationId>(),
                It.IsAny<ChatSelfId>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(groupConversation);

        var processor = new GroupStreamIngressProcessor(
            groupConversationRepository.Object,
            peerIdentityQueries.Object,
            senderKeyCryptographyService.Object,
            chatMessageWriter.Object);

        // ACT
        await processor.ProcessGroupMessageAsync(
            conversationId,
            senderPublicIdentityId,
            epoch: 1,
            ciphertext: new byte[] { 0x01, 0x02 },
            CancellationToken.None);

        // ASSERT - Message should not be persisted
        chatMessageWriter.Verify(w => w.AddGroupMessageAsync(
            It.IsAny<ChatConversationId>(),
            It.IsAny<ParticipantId>(),
            It.IsAny<string>(),
            It.IsAny<PublicMessageId>(),
            It.IsAny<DateTimeOffset>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task ProcessGroupMessageAsync_Throws_WhenIncomingEpochExceedsLocalEpoch()
    {
        // ARRANGE
        var groupConversationRepository = new Mock<IGroupConversationRepository>();
        var peerIdentityQueries = new Mock<IPeerIdentityQueries>();
        var senderKeyCryptographyService = new Mock<ISenderKeyCryptographyService>();
        var chatMessageWriter = new Mock<IChatMessageWriter>();

        var conversationId = Guid.NewGuid();
        var senderPublicIdentityId = Guid.NewGuid();
        var senderPeerId = new PeerId(1);
        
        peerIdentityQueries.Setup(q => q.GetPeerIdByPublicIdentityIdAsync(
                It.IsAny<PublicIdentityId>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(senderPeerId);

        var groupConversation = new GroupConversation(
            new ChatConversationId(conversationId),
            new GroupState(new ChatConversationId(conversationId), 1, null, RelayGroupPublicParamsBytes.FromBytes(new byte[32]), FixedTime, FixedTime),
            new ChatPeerId(999),
            members: new List<GroupMember>
            {
                new GroupMember(new ChatConversationId(conversationId), new RemoteParticipantId(new ChatPublicIdentityId(Guid.NewGuid()), new ChatPeerId(1)), GroupMemberRole.Member, FixedTime)
            });

        groupConversationRepository.Setup(r => r.GetByIdAsync(
                It.IsAny<ChatConversationId>(),
                It.IsAny<ChatSelfId>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(groupConversation);

        var processor = new GroupStreamIngressProcessor(
            groupConversationRepository.Object,
            peerIdentityQueries.Object,
            senderKeyCryptographyService.Object,
            chatMessageWriter.Object);

        // ACT
        var act = async () => await processor.ProcessGroupMessageAsync(
            conversationId,
            senderPublicIdentityId,
            epoch: 5, // Higher than local epoch (1)
            ciphertext: new byte[] { 0x01, 0x02 },
            CancellationToken.None);

        // ASSERT
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Epoch mismatch*");
    }

    [Test]
    public async Task ProcessGroupMessageAsync_PersistsDecryptedMessage_WhenMessageIsValid()
    {
        // ARRANGE
        var groupConversationRepository = new Mock<IGroupConversationRepository>();
        var peerIdentityQueries = new Mock<IPeerIdentityQueries>();
        var senderKeyCryptographyService = new Mock<ISenderKeyCryptographyService>();
        var chatMessageWriter = new Mock<IChatMessageWriter>();

        var conversationId = Guid.NewGuid();
        var senderPublicIdentityId = Guid.NewGuid();
        var senderPeerId = new PeerId(1);
        
        peerIdentityQueries.Setup(q => q.GetPeerIdByPublicIdentityIdAsync(
                It.IsAny<PublicIdentityId>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(senderPeerId);

        var groupConversation = new GroupConversation(
            new ChatConversationId(conversationId),
            new GroupState(new ChatConversationId(conversationId), 5, null, RelayGroupPublicParamsBytes.FromBytes(new byte[32]), FixedTime, FixedTime),
            new ChatPeerId(999),
            members: new List<GroupMember>
            {
                new GroupMember(new ChatConversationId(conversationId), new RemoteParticipantId(new ChatPublicIdentityId(Guid.NewGuid()), new ChatPeerId(1)), GroupMemberRole.Member, FixedTime)
            });

        groupConversationRepository.Setup(r => r.GetByIdAsync(
                It.IsAny<ChatConversationId>(),
                It.IsAny<ChatSelfId>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(groupConversation);

        var plaintext = System.Text.Encoding.UTF8.GetBytes("Hello, group!");
        senderKeyCryptographyService.Setup(s => s.DecryptGroupMessage(
                It.IsAny<CryptoConversationId>(),
                It.IsAny<CryptoPublicIdentity>(),
                It.IsAny<CryptoDeviceId>(),
                It.IsAny<byte[]>()))
            .Returns(plaintext);

        var processor = new GroupStreamIngressProcessor(
            groupConversationRepository.Object,
            peerIdentityQueries.Object,
            senderKeyCryptographyService.Object,
            chatMessageWriter.Object);

        // ACT
        await processor.ProcessGroupMessageAsync(
            conversationId,
            senderPublicIdentityId,
            epoch: 5, // Same as local epoch
            ciphertext: new byte[] { 0x01, 0x02 },
            CancellationToken.None);

        // ASSERT - Verify the message was persisted with correct content
        chatMessageWriter.Verify(w => w.AddGroupMessageAsync(
            It.IsAny<ChatConversationId>(),
            It.IsAny<ParticipantId>(),
            It.IsAny<string>(),
            It.IsAny<PublicMessageId>(),
            It.IsAny<DateTimeOffset>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }
}
