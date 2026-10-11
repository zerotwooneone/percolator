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
        var emoji = Emoji.Create("🚀").Value;

        var reaction = new ChatReaction(reactorId, emoji, timestamp);

        reaction.ReactorId.Should().Be(reactorId);
        reaction.Emoji.Should().Be(emoji);
        reaction.ReactedAtUnixMs.Should().Be(timestamp);
    }

    [Test]
    public void ChatReaction_ValueEquality_WorksAsExpected()
    {
        var reactorId = PublicIdentityId.New();
        var emoji1 = Emoji.Create("👍").Value;
        var emoji2 = Emoji.Create("👍").Value;

        var reaction1 = new ChatReaction(reactorId, emoji1, 1000);
        var reaction2 = new ChatReaction(reactorId, emoji2, 1000);

        reaction1.Should().Be(reaction2);
    }

    [TestCase("👍")]
    [TestCase("❤️")]
    [TestCase("🚀")]
    [TestCase("🎉")]
    [TestCase("👩🏽‍💻")] // ZWJ sequence + skin tone modifier is a single grapheme cluster
    public void Emoji_Create_ValidEmoji_Succeeds(string validEmoji)
    {
        var result = Emoji.Create(validEmoji);

        result.IsSuccess.Should().BeTrue();
        result.Value.Value.Should().Be(validEmoji);
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    public void Emoji_Create_NullOrWhitespace_Fails(string? invalidInput)
    {
        var result = Emoji.Create(invalidInput!);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("INVALID_EMOJI");
    }

    [Test]
    public void Emoji_Create_MultipleGraphemeClusters_Fails()
    {
        var result = Emoji.Create("👍🚀");

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("INVALID_EMOJI");
    }

    [Test]
    public void Emoji_Create_ExceedsMaxLength_Fails()
    {
        var longString = new string('A', 33);
        var result = Emoji.Create(longString);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("INVALID_EMOJI");
    }
}
