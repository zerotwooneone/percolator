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
public class PostReadReceiptHandlerTests
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
    public async Task Handle_Resolves_and_Writes_ReadReceipt()
    {
        // Arrange
        var lookup = ConversationLookupKey.ForDirectSession(Guid.NewGuid());
        var messageId = new MessageId(Guid.NewGuid());
        var sentAt = DateTimeOffset.UtcNow;
        var convo = MakeConversation();
        var selfIdentityId = 7;

        _resolver
            .Setup(r => r.ResolveAsync(lookup, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConversationResolution(convo, selfIdentityId));

        _writer
            .Setup(w => w.AddReadReceiptAsync(convo.Id, selfIdentityId, messageId, sentAt, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var handler = new PostReadReceiptHandler(_resolver.Object, _writer.Object);
        var cmd = new PostReadReceiptCommand(lookup, messageId, sentAt);

        // Act
        await handler.Handle(cmd, CancellationToken.None);

        // Assert
        _resolver.VerifyAll();
        _writer.VerifyAll();
    }

    [Test]
    public async Task Handle_Throws_when_multiple_keys()
    {
        // Arrange
        var lookup = new ConversationLookupKey(Guid.NewGuid(), new Pkh(new byte[32]), null);
        var messageId = new MessageId(Guid.NewGuid());
        var handler = new PostReadReceiptHandler(_resolver.Object, _writer.Object);
        var cmd = new PostReadReceiptCommand(lookup, messageId, DateTimeOffset.UtcNow);

        // Act
        Func<Task> act = async () => await handler.Handle(cmd, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<ArgumentException>();
        _resolver.Verify(r => r.ResolveAsync(It.IsAny<ConversationLookupKey>(), It.IsAny<CancellationToken>()), Times.Never);
        _writer.VerifyNoOtherCalls();
    }
}
