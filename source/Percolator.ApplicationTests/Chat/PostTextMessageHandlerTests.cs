using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using MediatR;
using Moq;
using Percolator.Application.Identity;
using Percolator.Application.Apps.Chat.Handlers;
using Percolator.Application.Network;
using Percolator.Chat;
using Percolator.Chat.App;
using Percolator.Chat.App.Commands;
using Percolator.Chat.ValueObjects;
using Percolator.Chat.Events;
using Percolator.Contracts;

namespace Percolator.Chat.Tests;

[TestFixture]
public class PostTextMessageHandlerTests
{
    private Mock<IConversationResolver> _resolver = null!;
    private Mock<IChatMessageWriter> _writer = null!;
    private Mock<IPublisher> _publisher = null!;
    private Mock<IRemoteEnvelopeSender> _sender = null!;
    private ActiveIdentityContext _active = null!;

    [SetUp]
    public void SetUp()
    {
        _resolver = new Mock<IConversationResolver>(MockBehavior.Strict);
        _writer = new Mock<IChatMessageWriter>(MockBehavior.Strict);
        _publisher = new Mock<IPublisher>(MockBehavior.Loose);
        _sender = new Mock<IRemoteEnvelopeSender>(MockBehavior.Loose);
        _active = new ActiveIdentityContext();
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

        _publisher
            .Setup(p => p.Publish(It.IsAny<TextMessagePostedEvent>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Allow network send fan-out via IRemoteEnvelopeSender to proceed without affecting assertions
        _sender
            .Setup(s => s.SendChatEnvelopeToPeerAsync(It.IsAny<Percolator.Contracts.ChatEnvelope>(), It.IsAny<RecipientRoute>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Some MediatR versions may route through the non-generic overload
        _publisher
            .Setup(p => p.Publish(It.IsAny<object>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var handler = new PostTextMessageHandler(
            _resolver.Object,
            _writer.Object,
            _sender.Object,
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
        var lookup = new ConversationLookupKey(null, null, null);
        var messageId = new MessageId(Guid.NewGuid());
        var handler = new PostTextMessageHandler(
            _resolver.Object,
            _writer.Object,
            _sender.Object,
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
