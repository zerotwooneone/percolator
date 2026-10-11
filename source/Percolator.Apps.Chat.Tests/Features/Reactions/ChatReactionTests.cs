using FluentAssertions;
using NUnit.Framework;
using Percolator.Apps.Chat.Features.Reactions;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Apps.Chat.Tests.Features.Reactions;

[TestFixture]
public class ChatReactionTests
{
    [Test]
    public void ChatReaction_PropertiesInitializedCorrectly()
    {
        var reactorId = PublicIdentityId.New();
        var timestamp = 123456789UL;
        var emoji = "🚀";

        var reaction = new ChatReaction(reactorId, emoji, timestamp);

        reaction.ReactorId.Should().Be(reactorId);
        reaction.Emoji.Should().Be(emoji);
        reaction.ReactedAtUnixMs.Should().Be(timestamp);
    }

    [Test]
    public void ChatReaction_ValueEquality_WorksAsExpected()
    {
        var reactorId = PublicIdentityId.New();
        var reaction1 = new ChatReaction(reactorId, "👍", 1000);
        var reaction2 = new ChatReaction(reactorId, "👍", 1000);

        reaction1.Should().Be(reaction2);
    }
}
