using FluentAssertions;
using MediatR;
using Moq;
using Percolator.Application.Apps.Chat.Handlers;
using Percolator.Application.Identity;
using Percolator.Chat.GroupMembership;
using Percolator.Chat.Messaging;
using Percolator.Chat.Messaging.App;
using Percolator.Chat.Messaging.App.Commands;
using Percolator.Chat.Messaging.Events;
using Percolator.Chat.Messaging.ValueObjects;
using Percolator.Identity;
using PublicMessageId = Percolator.Chat.Messaging.PublicMessageId;

namespace Percolator.ApplicationTests.Chat;

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
        var peer1 = new ChatPeerId(1);
        var selfId = new ChatSelfId(2);
        return new DirectConversation(
            new ConversationId(Guid.NewGuid()),
            peer1,
            selfId);
    }

    [Test]
    public async Task Handle_Resolves_and_Writes_TextMessage()
    {
        // Arrange
        var lookup = ConversationLookupKey.ForDirectSession(Guid.NewGuid());
        var messageId = new PublicMessageId(Guid.NewGuid());
        var content = "hello";
        var sentAt = DateTimeOffset.UtcNow;
        var convo = MakeConversation();
        var selfIdentityId = new ChatSelfId(42);
        var publicIdentityId = new Percolator.Chat.GroupLedger.PublicIdentityId(Guid.NewGuid());
        var selfParticipantId = new Percolator.Chat.GroupMembership.LocalParticipantId(publicIdentityId, selfIdentityId);

        _active.SetActiveIdentity(new Percolator.Identity.Model.IdentityRecord(new SelfId(42), new Percolator.Identity.PublicIdentityId(publicIdentityId.Value), new DeviceId(1), "self"));

        var selfIdentityQueries = new Mock<Percolator.Application.Chat.ISelfIdentityQueries>(MockBehavior.Strict);
        selfIdentityQueries.Setup(q => q.GetSelfIdentityPublicKeyAsync(new SelfId(42), It.IsAny<CancellationToken>()))
            .Returns(Task.FromResult<Percolator.Identity.PublicIdentityId?>(new Percolator.Identity.PublicIdentityId(publicIdentityId.Value)));

        _resolver
            .Setup(r => r.ResolveAsync(lookup, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DirectConversationResolution(convo, selfIdentityId));

        _writer
            .Setup(w => w.AddTextMessageAsync(convo.Id, selfParticipantId, content, messageId, sentAt, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _publisher
            .Setup(p => p.Publish(It.IsAny<object>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var handler = new PostTextMessageHandler(
            _resolver.Object,
            _writer.Object,
            _publisher.Object,
            selfIdentityQueries.Object);
        var cmd = new PostTextMessageCommand(lookup, messageId, content, sentAt, selfIdentityId);

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
        var lookup = new ConversationLookupKey(null, null);
        var messageId = new PublicMessageId(Guid.NewGuid());
        var handler = new PostTextMessageHandler(
            _resolver.Object,
            _writer.Object,
            _publisher.Object,
            Mock.Of<Percolator.Application.Chat.ISelfIdentityQueries>());
        var cmd = new PostTextMessageCommand(lookup, messageId, "x", DateTimeOffset.UtcNow, new ChatSelfId(1));

        // Act
        Func<Task> act = async () => await handler.Handle(cmd, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<ArgumentException>();
        _resolver.Verify(r => r.ResolveAsync(It.IsAny<ConversationLookupKey>(), It.IsAny<CancellationToken>()), Times.Never);
        _writer.VerifyNoOtherCalls();
    }
}
