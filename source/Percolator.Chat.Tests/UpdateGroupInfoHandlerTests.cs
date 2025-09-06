using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using Percolator.Chat;
using Percolator.Chat.App;
using Percolator.Chat.App.Commands;
using Percolator.Chat.App.Handlers;
using Percolator.Chat.ValueObjects;

namespace Percolator.Chat.Tests;

[TestFixture]
public class UpdateGroupInfoHandlerTests
{
    private Mock<IConversationResolver> _resolver = null!;
    private Mock<IConversationRepository> _repository = null!;

    [SetUp]
    public void SetUp()
    {
        _resolver = new Mock<IConversationResolver>(MockBehavior.Strict);
        _repository = new Mock<IConversationRepository>(MockBehavior.Strict);
    }

    private static Conversation MakeConversation()
    {
        var p1 = new ParticipantId(Guid.NewGuid());
        var p2 = new ParticipantId(Guid.NewGuid());
        return new Conversation(new ConversationId(Guid.NewGuid()), new[] { p1, p2 }, Array.Empty<Message>(), null);
    }

    [Test]
    public async Task Handle_Changes_Name_When_Provided()
    {
        // Arrange
        var lookup = ConversationLookupKey.ForGroup(Guid.NewGuid());
        var convo = MakeConversation();
        var selfIdentityId = 42;

        _resolver
            .Setup(r => r.ResolveAsync(lookup, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConversationResolution(convo, selfIdentityId));
        _repository
            .Setup(r => r.UpdateAsync(It.IsAny<Conversation>(), selfIdentityId))
            .Returns(Task.CompletedTask)
            .Callback<Conversation, int>((c, _) =>
            {
                c.Name.Should().Be("new-name");
            });

        var handler = new UpdateGroupInfoHandler(_resolver.Object, _repository.Object);
        var cmd = new UpdateGroupInfoCommand(lookup, "new-name");

        // Act
        await handler.Handle(cmd, CancellationToken.None);

        // Assert
        _resolver.VerifyAll();
        _repository.VerifyAll();
    }

    [Test]
    public async Task Handle_Throws_when_multiple_keys()
    {
        // Arrange
        var lookup = new ConversationLookupKey(Guid.NewGuid(), new Pkh(new byte[32]), null);
        var handler = new UpdateGroupInfoHandler(_resolver.Object, _repository.Object);
        var cmd = new UpdateGroupInfoCommand(lookup, null);

        // Act
        Func<Task> act = async () => await handler.Handle(cmd, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<ArgumentException>();
        _resolver.Verify(r => r.ResolveAsync(It.IsAny<ConversationLookupKey>(), It.IsAny<CancellationToken>()), Times.Never);
        _repository.VerifyNoOtherCalls();
    }
}
