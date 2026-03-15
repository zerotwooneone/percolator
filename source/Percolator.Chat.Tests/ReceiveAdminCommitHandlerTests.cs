using Moq;
using Percolator.Chat.App;
using Percolator.Chat.App.Commands;
using Percolator.Chat.App.Handlers;
using Percolator.Chat.ValueObjects;

namespace Percolator.Chat.Tests;

[TestFixture]
public class ReceiveAdminCommitHandlerTests
{
    private Mock<IConversationResolver> _resolver = null!;
    private Mock<IGroupAdminStateStore> _stateStore = null!;

    [SetUp]
    public void SetUp()
    {
        _resolver = new Mock<IConversationResolver>(MockBehavior.Strict);
        _stateStore = new Mock<IGroupAdminStateStore>(MockBehavior.Strict);
    }

    private static Conversation MakeConversation()
    {
        var p1 = new ParticipantId(Guid.NewGuid());
        var p2 = new ParticipantId(Guid.NewGuid());
        return new Conversation(new ConversationId(Guid.NewGuid()), new[] { p1, p2 }, Array.Empty<Message>(), null);
    }

    [Test]
    public async Task Commit_Succeeds_When_Sequence_Matches_And_Version_Continuity()
    {
        // Arrange
        var lookup = ConversationLookupKey.ForGroup(Guid.NewGuid());
        var convo = MakeConversation();
        var selfIdentityId = 1;
        var handler = new ReceiveAdminCommitHandler(_resolver.Object, _stateStore.Object);

        _resolver.Setup(r => r.ResolveAsync(lookup, It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new ConversationResolution(convo, selfIdentityId));

        _stateStore.Setup(s => s.InitializeIfMissingAsync(convo.Id.Value, It.IsAny<CancellationToken>()))
                   .Returns(Task.CompletedTask);

        _stateStore.Setup(s => s.TryCommitAsync(convo.Id.Value, 5UL, 11u, It.IsAny<CancellationToken>()))
                   .ReturnsAsync(true);

        var cmd = new ReceiveAdminCommitCommand(lookup, Guid.NewGuid(), new GroupKeyVersion(11), DateTimeOffset.UtcNow, 5UL, new byte[]{1,2});

        // Act
        await handler.Handle(cmd, CancellationToken.None);

        // Assert
        _resolver.VerifyAll();
        _stateStore.VerifyAll();
    }

    [Test]
    public async Task Commit_NoOp_When_Sequence_Mismatch()
    {
        // Arrange
        var lookup = ConversationLookupKey.ForGroup(Guid.NewGuid());
        var convo = MakeConversation();
        var selfIdentityId = 1;
        var handler = new ReceiveAdminCommitHandler(_resolver.Object, _stateStore.Object);

        _resolver.Setup(r => r.ResolveAsync(lookup, It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new ConversationResolution(convo, selfIdentityId));

        _stateStore.Setup(s => s.InitializeIfMissingAsync(convo.Id.Value, It.IsAny<CancellationToken>()))
                   .Returns(Task.CompletedTask);

        _stateStore.Setup(s => s.TryCommitAsync(convo.Id.Value, 8UL, 12u, It.IsAny<CancellationToken>()))
                   .ReturnsAsync(false);

        var cmd = new ReceiveAdminCommitCommand(lookup, Guid.NewGuid(), new GroupKeyVersion(12), DateTimeOffset.UtcNow, 8UL, new byte[]{1,2});

        // Act
        await handler.Handle(cmd, CancellationToken.None);

        // Assert
        _resolver.VerifyAll();
        _stateStore.VerifyAll();
    }
}
