using FluentAssertions;
using NUnit.Framework;
using Percolator.Apps.Chat.Models;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.PluginSdk;

namespace Percolator.Apps.Chat.Tests.Models;

[TestFixture]
public class ChatMessageRecordTests
{
    private static readonly ChannelId TestChannelId = ChannelId.New();
    private static readonly PayloadId MessageId = PayloadId.New();
    private static readonly PublicIdentityId Author = PublicIdentityId.New();
    private static readonly PublicIdentityId OtherUser = PublicIdentityId.New();
    private const ulong SentAtMs = 1_000_000_000;
    private const ulong MaxEditWindowMs = 24UL * 60 * 60 * 1000;

    [Test]
    public void ApplyEdit_ByAuthor_Within24Hours_SucceedsAndUpdatesContent()
    {
        var record = new ChatMessageRecord(
            MessageId,
            TestChannelId,
            Author,
            SentAtMs,
            "Initial text");

        var editTime = SentAtMs + 10_000;
        var success = record.ApplyEdit("Edited text", Array.Empty<MentionDto>(), editTime, Author);

        success.Should().BeTrue();
        record.Content.Should().Be("Edited text");
        record.EditedAtUnixMs.Should().Be(editTime);
    }

    [Test]
    public void ApplyEdit_ByNonAuthor_Fails()
    {
        var record = new ChatMessageRecord(
            MessageId,
            TestChannelId,
            Author,
            SentAtMs,
            "Initial text");

        var editTime = SentAtMs + 10_000;
        var success = record.ApplyEdit("Malicious edit", Array.Empty<MentionDto>(), editTime, OtherUser);

        success.Should().BeFalse();
        record.Content.Should().Be("Initial text");
        record.EditedAtUnixMs.Should().BeNull();
    }

    [TestCase(MaxEditWindowMs + 1)]
    [TestCase(MaxEditWindowMs + 10_000)]
    public void ApplyEdit_Past24HourWindow_Fails(ulong deltaMs)
    {
        var record = new ChatMessageRecord(
            MessageId,
            TestChannelId,
            Author,
            SentAtMs,
            "Initial text");

        var editTime = SentAtMs + deltaMs;
        var success = record.ApplyEdit("Late edit", Array.Empty<MentionDto>(), editTime, Author);

        success.Should().BeFalse();
        record.Content.Should().Be("Initial text");
    }

    [Test]
    public void ApplyTombstone_ClearsContentAndAttachments_AndSetsIsTombstone()
    {
        var attachment = new MediaAttachmentDto(
            new BlobId("abcdef0123456789"),
            "photo.jpg",
            "image/jpeg",
            1024,
            100,
            100,
            Array.Empty<byte>(),
            null);

        var record = new ChatMessageRecord(
            MessageId,
            TestChannelId,
            Author,
            SentAtMs,
            "Secret text",
            attachments: new[] { attachment });

        var tombstoneTime = SentAtMs + 500;
        var success = record.ApplyTombstone(Author, tombstoneTime);

        success.Should().BeTrue();
        record.IsTombstone.Should().BeTrue();
        record.Content.Should().Be("This message was deleted");
        record.Attachments.Should().BeEmpty();
        record.DeletedAtUnixMs.Should().Be(tombstoneTime);
        record.DeletedBy.Should().Be(Author);
    }

    [Test]
    public void AddAndRemoveReactions_AreIdempotent()
    {
        var record = new ChatMessageRecord(
            MessageId,
            TestChannelId,
            Author,
            SentAtMs,
            "Hello");

        var reaction = new ReactionUpdateDto(
            MessageId,
            TestChannelId,
            Author,
            "👍",
            ReactionAction.Add,
            SentAtMs + 100);

        record.AddOrUpdateReaction(reaction);
        record.AddOrUpdateReaction(reaction); // duplicate add

        record.Reactions.Should().HaveCount(1);

        record.RemoveReaction(Author, "👍");
        record.RemoveReaction(Author, "👍"); // duplicate remove

        record.Reactions.Should().BeEmpty();
    }
}
