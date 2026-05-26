using FluentAssertions;
using Moq;
using MediatR;
using Percolator.Chat.App;
using Percolator.Chat.App.Commands;
using Percolator.Chat.Events;
using Percolator.Chat.ValueObjects;
using Percolator.Chat;

namespace Percolator.Chat.Tests;

[TestFixture]
public class PostEmojiAnnotationHandlerTests
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
        var peer1 = new ParticipantId(Guid.NewGuid());
        var peer2 = new ParticipantId(Guid.NewGuid());
        return new DirectConversation(
            new ConversationId(Guid.NewGuid()),
            peer1,
            peer2);
    }

    [Test]
    public async Task Handle_Resolves_and_Writes_EmojiAnnotation()
    {
        // Arrange
        var lookup = ConversationLookupKey.ForDirectSession(Guid.NewGuid());
        var messageId = new MessageId(Guid.NewGuid());
        var emoji = "👍";
        var sentAt = DateTimeOffset.UtcNow;
        var convo = MakeConversation();
        var selfIdentityId = 101;
        var selfParticipantId = new ParticipantId(Guid.NewGuid());

        _resolver
            .Setup(r => r.ResolveAsync(lookup, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DirectConversationResolution(convo, selfIdentityId));

        _selfParticipantIdProvider
            .Setup(p => p.Get())
            .Returns(selfParticipantId);

        _writer
            .Setup(w => w.AddEmojiAnnotationAsync(convo.Id, selfIdentityId, selfParticipantId, messageId, emoji, sentAt, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _publisher
            .Setup(p => p.Publish(It.IsAny<EmojiAnnotationPostedEvent>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var handler = new PostEmojiAnnotationHandler(_resolver.Object, _writer.Object, _publisher.Object, _selfParticipantIdProvider.Object);
        var cmd = new PostEmojiAnnotationCommand(lookup, messageId, emoji, sentAt);

        // Act
        await handler.Handle(cmd, CancellationToken.None);

        // Assert
        _resolver.VerifyAll();
        _writer.VerifyAll();
        _publisher.Verify(p => p.Publish(It.IsAny<EmojiAnnotationPostedEvent>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task Handle_Throws_when_multiple_keys()
    {
        // Arrange
        var lookup = new ConversationLookupKey(Guid.NewGuid(), new Pkh(new byte[32]));
        var messageId = new MessageId(Guid.NewGuid());
        var handler = new PostEmojiAnnotationHandler(_resolver.Object, _writer.Object, _publisher.Object, _selfParticipantIdProvider.Object);
        var cmd = new PostEmojiAnnotationCommand(lookup, messageId, "😀", DateTimeOffset.UtcNow);

        // Act
        Func<Task> act = async () => await handler.Handle(cmd, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<ArgumentException>();
        _resolver.Verify(r => r.ResolveAsync(It.IsAny<ConversationLookupKey>(), It.IsAny<CancellationToken>()), Times.Never);
        _writer.VerifyNoOtherCalls();
    }
}
