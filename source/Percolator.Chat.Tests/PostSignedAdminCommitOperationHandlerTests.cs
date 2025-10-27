using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediatR;
using Moq;
using NUnit.Framework;
using Percolator.Chat.App;
using Percolator.Chat.App.Commands;
using Percolator.Chat.App.Handlers;
using Percolator.Chat.Events;
using Percolator.Chat.ValueObjects;

namespace Percolator.Chat.Tests;

[TestFixture]
public class PostSignedAdminCommitOperationHandlerTests
{
    private Mock<IConversationResolver> _resolver = null!;
    private Mock<IPublisher> _publisher = null!;

    [SetUp]
    public void SetUp()
    {
        _resolver = new Mock<IConversationResolver>(MockBehavior.Strict);
        _publisher = new Mock<IPublisher>(MockBehavior.Loose);
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
    public async Task Handle_Publishes_Event_Excluding_Self()
    {
        // Arrange
        var lookup = ConversationLookupKey.ForDirectSession(Guid.NewGuid());
        var opId = Guid.NewGuid();
        var sentUtc = DateTimeOffset.UtcNow;
        var convo = MakeConversation();
        var selfIdentityId = 5;

        _resolver
            .Setup(r => r.ResolveAsync(lookup, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConversationResolution(convo, selfIdentityId));

        _publisher
            .Setup(p => p.Publish(It.IsAny<SignedAdminCommitOperationPostedEvent>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var handler = new PostSignedAdminCommitOperationHandler(_resolver.Object, _publisher.Object);
        var cmd = new PostSignedAdminCommitOperationCommand(
            lookup,
            opId,
            7,
            sentUtc,
            new byte[] { 9, 9, 9 },
            null);

        // Act
        await handler.Handle(cmd, CancellationToken.None);

        // Assert
        _resolver.VerifyAll();
        _publisher.Verify(p => p.Publish(It.IsAny<SignedAdminCommitOperationPostedEvent>(), It.IsAny<CancellationToken>()), Times.Once);
    }
}
