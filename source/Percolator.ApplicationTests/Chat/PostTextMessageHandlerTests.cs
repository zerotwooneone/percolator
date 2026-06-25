using FluentAssertions;
using MediatR;
using Moq;
using Percolator.Application.Identity;
using Percolator.Application.Apps.Chat.Handlers;
using Percolator.Chat.Messaging;
using Percolator.Chat.Messaging.App;
using Percolator.Chat.Messaging.App.Commands;
using Percolator.Chat.Messaging.Events;
using Percolator.Chat.Messaging.ValueObjects;
using Percolator.Identity;

namespace Percolator.Chat.Tests;

[TestFixture]
public class PostTextMessageHandlerTests
{
    private Mock<IDirectConversationResolver> _resolver = null!;
    private Mock<IChatMessageWriter> _writer = null!;
    private Mock<IPublisher> _publisher = null!;
    private ActiveIdentityContext _active = null!;

    [SetUp]
    public void SetUp()
    {
        _resolver = new Mock<IDirectConversationResolver>(MockBehavior.Strict);
        _writer = new Mock<IChatMessageWriter>(MockBehavior.Strict);
        _publisher = new Mock<IPublisher>(MockBehavior.Loose);
        _active = new ActiveIdentityContext();
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
    public async Task Handle_Resolves_and_Writes_TextMessage()
    {
        // Arrange
        var lookup = ConversationLookupKey.ForDirectSession(Guid.NewGuid());
        var messageId = new MessageId(Guid.NewGuid());
        var content = "hello";
        var sentAt = DateTimeOffset.UtcNow;
        var convo = MakeConversation();
        var selfIdentityId = 42;
        var selfParticipantId = new ParticipantId(Guid.NewGuid());

        _active.SetActiveIdentity(new Percolator.Identity.Model.IdentityRecord(selfParticipantId.Value, "self") { PublicIdentityId = new PublicIdentityId(selfParticipantId.Value) });

        _resolver
            .Setup(r => r.ResolveAsync(lookup, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DirectConversationResolution(convo, selfIdentityId));

        _writer
            .Setup(w => w.AddTextMessageAsync(convo.Id, selfIdentityId, selfParticipantId, content, messageId, sentAt, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _publisher
            .Setup(p => p.Publish(It.IsAny<TextMessagePostedEvent>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Some MediatR versions may route through the non-generic overload
        _publisher
            .Setup(p => p.Publish(It.IsAny<object>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var handler = new PostTextMessageHandler(
            _resolver.Object,
            _writer.Object,
            _publisher.Object,
            _active);
        var cmd = new PostTextMessageCommand(lookup, messageId, content, sentAt);

        // Act
        await handler.Handle(cmd, CancellationToken.None);

        // Assert
        _resolver.VerifyAll();
        _writer.VerifyAll();
        _publisher.Verify(p => p.Publish(It.IsAny<TextMessagePostedEvent>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task Handle_Throws_when_no_routing_key()
    {
        // Arrange
        var lookup = new ConversationLookupKey(null, null);
        var messageId = new MessageId(Guid.NewGuid());
        var handler = new PostTextMessageHandler(
            _resolver.Object,
            _writer.Object,
            _publisher.Object,
            _active);
        var cmd = new PostTextMessageCommand(lookup, messageId, "x", DateTimeOffset.UtcNow);

        // Act
        Func<Task> act = async () => await handler.Handle(cmd, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<ArgumentException>();
        _resolver.Verify(r => r.ResolveAsync(It.IsAny<ConversationLookupKey>(), It.IsAny<CancellationToken>()), Times.Never);
        _writer.VerifyNoOtherCalls();
    }
}
