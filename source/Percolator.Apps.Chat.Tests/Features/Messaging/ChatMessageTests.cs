using FluentAssertions;
using NUnit.Framework;
using Percolator.Apps.Chat.Features.Media;
using Percolator.Apps.Chat.Features.Messaging;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.PluginSdk;

namespace Percolator.Apps.Chat.Tests.Features.Messaging;

[TestFixture]
public class ChatMessageTests
{
    private static readonly ChannelId TestChannelId = ChannelId.New();
    private static readonly PayloadId MessageId = PayloadId.New();
    private static readonly PublicIdentityId Author = PublicIdentityId.New();
    private static readonly PublicIdentityId OtherUser = PublicIdentityId.New();
    private static readonly PublicIdentityId AdminUser = PublicIdentityId.New();
    private const ulong SentAtMs = 1_000_000_000;
    private const ulong MaxEditWindowMs = 24UL * 60 * 60 * 1000;

    [Test]
    public void ApplyEdit_ByAuthor_Within24Hours_SucceedsAndUpdatesContent()
    {
        var message = new ChatMessage(
            MessageId,
            TestChannelId,
            Author,
            SentAtMs,
            "Initial text");

        var editTime = SentAtMs + 10_000;
        var result = message.ApplyEdit("Edited text", Array.Empty<MentionDto>(), editTime, Author);

        result.IsSuccess.Should().BeTrue();
        message.Content.Should().Be("Edited text");
        message.EditedAtUnixMs.Should().Be(editTime);
    }

    [Test]
    public void ApplyEdit_ByNonAuthor_Fails()
    {
        var message = new ChatMessage(
            MessageId,
            TestChannelId,
            Author,
            SentAtMs,
            "Initial text");

        var editTime = SentAtMs + 10_000;
        var result = message.ApplyEdit("Malicious edit", Array.Empty<MentionDto>(), editTime, OtherUser);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("UNAUTHORIZED_AUTHOR");
        message.Content.Should().Be("Initial text");
        message.EditedAtUnixMs.Should().BeNull();
    }

    [TestCase(MaxEditWindowMs + 1)]
    [TestCase(MaxEditWindowMs + 10_000)]
    public void ApplyEdit_Past24HourWindow_Fails(ulong deltaMs)
    {
        var message = new ChatMessage(
            MessageId,
            TestChannelId,
            Author,
            SentAtMs,
            "Initial text");

        var editTime = SentAtMs + deltaMs;
        var result = message.ApplyEdit("Late edit", Array.Empty<MentionDto>(), editTime, Author);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("EDIT_WINDOW_EXPIRED");
        message.Content.Should().Be("Initial text");
    }

    [Test]
    public void ApplyEdit_WhenAlreadyTombstoned_Fails()
    {
        var message = new ChatMessage(
            MessageId,
            TestChannelId,
            Author,
            SentAtMs,
            "Initial text");

        message.ApplyTombstone(Author, SentAtMs + 1000);

        var result = message.ApplyEdit("Attempt edit tombstoned", Array.Empty<MentionDto>(), SentAtMs + 2000, Author);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("ALREADY_TOMBSTONED");
    }

    [Test]
    public void ApplyTombstone_ByAuthor_Within24Hours_SucceedsWithAuthorPlaceholder()
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

        var message = new ChatMessage(
            MessageId,
            TestChannelId,
            Author,
            SentAtMs,
            "Secret text",
            attachments: new[] { attachment });

        var tombstoneTime = SentAtMs + 500;
        var result = message.ApplyTombstone(Author, tombstoneTime, isChannelAdmin: false);

        result.IsSuccess.Should().BeTrue();
        message.IsTombstone.Should().BeTrue();
        message.Content.Should().Be("This message was deleted");
        message.Attachments.Should().BeEmpty();
        message.DeletedAtUnixMs.Should().Be(tombstoneTime);
        message.DeletedBy.Should().Be(Author);
    }

    [Test]
    public void ApplyTombstone_ByChannelAdmin_Within24Hours_SucceedsWithAdminPlaceholder()
    {
        var message = new ChatMessage(
            MessageId,
            TestChannelId,
            Author,
            SentAtMs,
            "Inappropriate group message");

        var tombstoneTime = SentAtMs + 500;
        var result = message.ApplyTombstone(AdminUser, tombstoneTime, isChannelAdmin: true);

        result.IsSuccess.Should().BeTrue();
        message.IsTombstone.Should().BeTrue();
        message.Content.Should().Be("This message was deleted by a channel administrator");
        message.DeletedAtUnixMs.Should().Be(tombstoneTime);
        message.DeletedBy.Should().Be(AdminUser);
    }

    [Test]
    public void ApplyTombstone_ByNonAuthorNonAdmin_Fails()
    {
        var message = new ChatMessage(
            MessageId,
            TestChannelId,
            Author,
            SentAtMs,
            "Normal message");

        var tombstoneTime = SentAtMs + 500;
        var result = message.ApplyTombstone(OtherUser, tombstoneTime, isChannelAdmin: false);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("UNAUTHORIZED_AUTHOR");
        message.IsTombstone.Should().BeFalse();
    }

    [TestCase(MaxEditWindowMs + 1)]
    [TestCase(MaxEditWindowMs + 10_000)]
    public void ApplyTombstone_Past24Hours_Fails(ulong deltaMs)
    {
        var message = new ChatMessage(
            MessageId,
            TestChannelId,
            Author,
            SentAtMs,
            "Old message");

        var tombstoneTime = SentAtMs + deltaMs;
        var result = message.ApplyTombstone(Author, tombstoneTime, isChannelAdmin: false);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("TOMBSTONE_WINDOW_EXPIRED");
        message.IsTombstone.Should().BeFalse();
    }

    [Test]
    public void ApplyTombstone_WhenAlreadyTombstoned_Fails()
    {
        var message = new ChatMessage(
            MessageId,
            TestChannelId,
            Author,
            SentAtMs,
            "Initial text");

        message.ApplyTombstone(Author, SentAtMs + 1000);

        var result = message.ApplyTombstone(Author, SentAtMs + 2000);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("ALREADY_TOMBSTONED");
    }

    [Test]
    public void AddAndRemoveReactions_AreIdempotent()
    {
        var message = new ChatMessage(
            MessageId,
            TestChannelId,
            Author,
            SentAtMs,
            "Hello");

        message.AddReaction(Author, "👍", SentAtMs + 100);
        message.AddReaction(Author, "👍", SentAtMs + 200); // duplicate add updates timestamp

        message.Reactions.Should().HaveCount(1);
        message.Reactions[0].Emoji.Should().Be("👍");
        message.Reactions[0].ReactedAtUnixMs.Should().Be(SentAtMs + 200);

        message.RemoveReaction(Author, "👍");
        message.RemoveReaction(Author, "👍"); // duplicate remove

        message.Reactions.Should().BeEmpty();
    }
}
