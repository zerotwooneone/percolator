using FluentAssertions;
using Moq;
using MediatR;
using Percolator.Chat.GroupMembership;
using Percolator.Chat.Messaging;
using Percolator.Chat.Messaging.App;
using Percolator.Chat.Messaging.App.Commands;
using Percolator.Chat.Messaging.App.Handlers;
using Percolator.Chat.Messaging.Events;
using Percolator.Chat.Messaging.ValueObjects;

namespace Percolator.Chat.Tests;

[TestFixture]
public class PostDeliveredReceiptHandlerTests
{
    private Mock<IDirectConversationResolver> _resolver = null!;
    private Mock<IChatMessageWriter> _writer = null!;
    private Mock<IPublisher> _publisher = null!;
    private Mock<ISelfParticipantIdProvider> _selfParticipantIdProvider = null!;

    [SetUp]
    public void SetUp()
    {
        _resolver = new Mock<IDirectConversationResolver>(MockBehavior.Strict);
        _writer = new Mock<IChatMessageWriter>(MockBehavior.Strict);
        _publisher = new Mock<IPublisher>(MockBehavior.Loose);
        _selfParticipantIdProvider = new Mock<ISelfParticipantIdProvider>(MockBehavior.Strict);
    }

    private static DirectConversation MakeConversation()
    {
        var peer1 = new ChatPeerId(Guid.NewGuid());
        var peer2 = new ChatPeerId(Guid.NewGuid());
        return new DirectConversation(
            new ConversationId(Guid.NewGuid()),
            peer1,
            peer2);
    }

    [Test]
    public async Task Handle_Resolves_and_Writes_DeliveredReceipt()
    {
        // Arrange
        var lookup = ConversationLookupKey.ForDirectSession(Guid.NewGuid());
        var messageId = new MessageId(Guid.NewGuid());
        var deliveredAt = DateTimeOffset.UtcNow;
        var convo = MakeConversation();
        var selfIdentityId = 7;
        var selfParticipantId = new ChatPeerId(Guid.NewGuid());

        _resolver
            .Setup(r => r.ResolveAsync(lookup, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DirectConversationResolution(convo, selfIdentityId));

        _selfParticipantIdProvider
            .Setup(p => p.Get())
            .Returns(selfParticipantId);

        _writer
            .Setup(w => w.AddDeliveredReceiptAsync(convo.Id, selfIdentityId, selfParticipantId, messageId, deliveredAt, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _publisher
            .Setup(p => p.Publish(It.IsAny<DeliveredReceiptPostedEvent>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var handler = new PostDeliveredReceiptHandler(_resolver.Object, _writer.Object, _publisher.Object, _selfParticipantIdProvider.Object);
        var cmd = new PostDeliveredReceiptCommand(lookup, messageId, deliveredAt);

        // Act
        await handler.Handle(cmd, CancellationToken.None);

        // Assert
        _resolver.VerifyAll();
        _writer.VerifyAll();
        _publisher.Verify(p => p.Publish(It.IsAny<DeliveredReceiptPostedEvent>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task Handle_Throws_when_multiple_keys()
    {
        // Arrange
        var lookup = new ConversationLookupKey(Guid.NewGuid(), Pkh.FromBytes(new byte[32]));
        var messageId = new MessageId(Guid.NewGuid());
        var handler = new PostDeliveredReceiptHandler(_resolver.Object, _writer.Object, _publisher.Object, _selfParticipantIdProvider.Object);
        var cmd = new PostDeliveredReceiptCommand(lookup, messageId, DateTimeOffset.UtcNow);

        // Act
        Func<Task> act = async () => await handler.Handle(cmd, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<ArgumentException>();
        _resolver.Verify(r => r.ResolveAsync(It.IsAny<ConversationLookupKey>(), It.IsAny<CancellationToken>()), Times.Never);
        _writer.VerifyNoOtherCalls();
    }
}
