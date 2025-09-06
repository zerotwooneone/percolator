using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using Percolator.Chat;
using Percolator.Chat.App;
using Percolator.Chat.App.Commands;
using Percolator.Chat.ValueObjects;

namespace Percolator.Chat.Tests;

[TestFixture]
public class PostTextMessageHandlerTests
{
    private Mock<IConversationResolver> _resolver = null!;
    private Mock<IChatMessageWriter> _writer = null!;

    [SetUp]
    public void SetUp()
    {
        _resolver = new Mock<IConversationResolver>(MockBehavior.Strict);
        _writer = new Mock<IChatMessageWriter>(MockBehavior.Strict);
    }

    private static Conversation MakeConversation()
    {
        var participants = new[] { new ParticipantId(Guid.NewGuid()), new ParticipantId(Guid.NewGuid()) };
        return new Conversation(
            new ConversationId(Guid.NewGuid()),
            participants,
            Array.Empty<Message>(),
            null);
    }

    [Test]
    public async Task Handle_Resolves_and_Writes_TextMessage()
    {
        // Arrange
        var lookup = ConversationLookupKey.ForDirectSession(Guid.NewGuid());
        var messageId = new MessageId(Guid.NewGuid());
        var content = "hello";
        var sentAt = DateTimeOffset.UtcNow;
        var convo = MakeConversation();
        var selfIdentityId = 42;

        _resolver
            .Setup(r => r.ResolveAsync(lookup, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConversationResolution(convo, selfIdentityId));

        _writer
            .Setup(w => w.AddTextMessageAsync(convo.Id, selfIdentityId, content, messageId, sentAt, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var handler = new PostTextMessageHandler(_resolver.Object, _writer.Object);
        var cmd = new PostTextMessageCommand(lookup, messageId, content, sentAt);

        // Act
        await handler.Handle(cmd, CancellationToken.None);

        // Assert
        _resolver.VerifyAll();
        _writer.VerifyAll();
    }

    [Test]
    public async Task Handle_Throws_when_no_routing_key()
    {
        // Arrange
        var lookup = new ConversationLookupKey(null, null, null);
        var messageId = new MessageId(Guid.NewGuid());
        var handler = new PostTextMessageHandler(_resolver.Object, _writer.Object);
        var cmd = new PostTextMessageCommand(lookup, messageId, "x", DateTimeOffset.UtcNow);

        // Act
        Func<Task> act = async () => await handler.Handle(cmd, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<ArgumentException>();
        _resolver.Verify(r => r.ResolveAsync(It.IsAny<ConversationLookupKey>(), It.IsAny<CancellationToken>()), Times.Never);
        _writer.VerifyNoOtherCalls();
    }
}
