using FluentAssertions;
using NUnit.Framework;
using Percolator.Apps.Chat.Features.Messaging;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Apps.Chat.Tests.Features.Messaging;

[TestFixture]
public class MentionSpanTests
{
    private static readonly PublicIdentityId AliceId = PublicIdentityId.New();

    [TestCase("Hello @alice", 6, 6, 6, 6, "@alice")]
    [TestCase("👋 @alice", 3, 6, 5, 6, "@alice")] // 👋 is 2 chars (4 UTF-8 bytes) + 1 char space = byte 5
    [TestCase("🎉🎊 @alice", 5, 6, 9, 6, "@alice")] // 2 chars + 2 chars + 1 space = 5 chars (4+4+1 = 9 UTF-8 bytes)
    [TestCase("こんにちは @alice", 6, 6, 16, 6, "@alice")] // 5 kanji/hiragana chars (15 UTF-8 bytes) + 1 space = byte 16
    public void CalculateUtf8Span_WithMultibyteText_PreservesUtf8ByteBoundaries(
        string fullText,
        int charStartIndex,
        int charLength,
        int expectedByteStart,
        int expectedByteLength,
        string expectedExtractedText)
    {
        var mention = MentionHelper.Create(fullText, charStartIndex, charLength, AliceId);

        mention.StartOffset.Should().Be(expectedByteStart);
        mention.Length.Should().Be(expectedByteLength);

        var extracted = MentionHelper.ExtractMentionText(fullText, mention);
        extracted.Should().Be(expectedExtractedText);
    }

    [Test]
    public void IsValidSpan_OutOfBoundsSpan_ReturnsFalse()
    {
        var text = "Hello world";
        var outOfBoundsMention = new MentionDto(AliceId, 20, 5);
        MentionHelper.IsValidSpan(text, outOfBoundsMention).Should().BeFalse();
    }

    [Test]
    public void MentionDto_NegativeOffsetOrLength_ThrowsArgumentOutOfRangeException()
    {
        var actNegativeOffset = () => new MentionDto(AliceId, -1, 5);
        actNegativeOffset.Should().Throw<ArgumentOutOfRangeException>()
            .WithMessage("*StartOffset must be non-negative*");

        var actZeroLength = () => new MentionDto(AliceId, 0, 0);
        actZeroLength.Should().Throw<ArgumentOutOfRangeException>()
            .WithMessage("*Length must be positive*");
    }
}
