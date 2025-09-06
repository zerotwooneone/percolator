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
public class UpdateGroupMembershipHandlerTests
{
    private Mock<IConversationResolver> _resolver = null!;
    private Mock<IConversationRepository> _repository = null!;
    private Mock<ISelfParticipantIdProvider> _selfProvider = null!;

    [SetUp]
    public void SetUp()
    {
        _resolver = new Mock<IConversationResolver>(MockBehavior.Strict);
        _repository = new Mock<IConversationRepository>(MockBehavior.Strict);
        _selfProvider = new Mock<ISelfParticipantIdProvider>(MockBehavior.Strict);
    }

    private static Conversation MakeConversation(out ParticipantId p1, out ParticipantId p2)
    {
        p1 = new ParticipantId(Guid.NewGuid());
        p2 = new ParticipantId(Guid.NewGuid());
        return new Conversation(new ConversationId(Guid.NewGuid()), new[] { p1, p2 }, Array.Empty<Message>(), null);
    }

    [Test]
    public async Task Handle_Adds_and_Removes_and_Leaves()
    {
        // Arrange
        var lookup = ConversationLookupKey.ForGroup(Guid.NewGuid());
        var convo = MakeConversation(out var p1, out var p2);
        var self = new ParticipantId(Guid.NewGuid());
        var toAdd = new ParticipantId(Guid.NewGuid());
        var toRemove = p2; // remove existing
        var selfIdentityId = 77;

        _resolver
            .Setup(r => r.ResolveAsync(lookup, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConversationResolution(convo, selfIdentityId));
        _selfProvider
            .Setup(s => s.Get())
            .Returns(self);
        _repository
            .Setup(r => r.UpdateAsync(It.IsAny<Conversation>(), selfIdentityId))
            .Returns(Task.CompletedTask)
            .Callback<Conversation, int>((c, _) =>
            {
                c.Participants.Should().Contain(p1);
                c.Participants.Should().Contain(toAdd);
                c.Participants.Should().NotContain(toRemove);
                c.Participants.Should().NotContain(self);
            });

        var handler = new UpdateGroupMembershipHandler(_resolver.Object, _repository.Object, _selfProvider.Object);
        var cmd = new UpdateGroupMembershipCommand(lookup, new[] { toAdd }, new[] { toRemove }, true);

        // Act
        await handler.Handle(cmd, CancellationToken.None);

        // Assert
        _resolver.VerifyAll();
        _selfProvider.VerifyAll();
        _repository.VerifyAll();
    }

    [Test]
    public async Task Handle_Throws_when_multiple_keys()
    {
        // Arrange
        var lookup = new ConversationLookupKey(Guid.NewGuid(), new Pkh(new byte[32]), null);
        var handler = new UpdateGroupMembershipHandler(_resolver.Object, _repository.Object, _selfProvider.Object);
        var cmd = new UpdateGroupMembershipCommand(lookup, Array.Empty<ParticipantId>(), Array.Empty<ParticipantId>(), false);

        // Act
        Func<Task> act = async () => await handler.Handle(cmd, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<ArgumentException>();
        _resolver.Verify(r => r.ResolveAsync(It.IsAny<ConversationLookupKey>(), It.IsAny<CancellationToken>()), Times.Never);
        _repository.VerifyNoOtherCalls();
        _selfProvider.VerifyNoOtherCalls();
    }
}
