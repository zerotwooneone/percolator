using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;
using Percolator.Chat.App;
using Percolator.Chat.ValueObjects;
using Percolator.Infrastructure.Chat;
using Percolator.Infrastructure.Persistence;

namespace Percolator.InfrastructureTests.Chat;

[TestFixture]
public class SqliteChatMessageWriterTests
{
    private static PercolatorDbContext CreateDbContext(out SqliteConnection connection)
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var options = new DbContextOptionsBuilder<PercolatorDbContext>()
            .UseSqlite(connection)
            .Options;
        var ctx = new PercolatorDbContext(options);
        ctx.Database.EnsureCreated();
        if (!ctx.SelfIdentities.Any())
        {
            ctx.SelfIdentities.Add(new SelfIdentityDbo { Id = 1, PeerId = Guid.NewGuid(), Name = "default" });
            ctx.SaveChanges();
        }

        return ctx;
    }

    private static (ConversationId conversationId, Guid otherPeerId) SeedConversation(PercolatorDbContext ctx, int selfIdentityId)
    {
        var self = ctx.SelfIdentities.Single(si => si.Id == selfIdentityId);
        var conversationId = Guid.NewGuid();
        var otherPeerId = Guid.NewGuid();

        ctx.Conversations.Add(new ConversationDbo
        {
            Id = conversationId,
            ChannelId = Guid.NewGuid().ToByteArray(),
            Name = "chat",
            SelfIdentityId = selfIdentityId,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        ctx.ConversationParticipants.Add(new ConversationParticipantDbo
        {
            ConversationId = conversationId,
            ParticipantId = self.PeerId
        });
        ctx.ConversationParticipants.Add(new ConversationParticipantDbo
        {
            ConversationId = conversationId,
            ParticipantId = otherPeerId
        });
        ctx.SaveChanges();

        return (new ConversationId(conversationId), otherPeerId);
    }

    [Test]
    public async Task AddDeliveredReceipt_is_idempotent_per_recipient_and_message()
    {
        var ctx = CreateDbContext(out var conn);
        await using var _ = conn;
        var (conversationId, _) = SeedConversation(ctx, 1);
        var writer = new SqliteChatMessageWriter(ctx);
        var messageId = new MessageId(Guid.NewGuid());
        var deliveredAt = DateTimeOffset.UtcNow;
        var ct = CancellationToken.None;

        await writer.AddDeliveredReceiptAsync(conversationId, 1, messageId, deliveredAt, ct);
        await writer.AddDeliveredReceiptAsync(conversationId, 1, messageId, deliveredAt, ct);

        var count = await ctx.DeliveredReceipts.CountAsync(r => r.ConversationId == conversationId.Value && r.MessageGuid == messageId.Value);
        count.Should().Be(1);
    }

    [Test]
    public async Task AddTextMessage_is_idempotent_per_message_id()
    {
        var ctx = CreateDbContext(out var conn);
        await using var _ = conn;
        var (conversationId, _) = SeedConversation(ctx, 1);
        var writer = new SqliteChatMessageWriter(ctx);
        var messageId = new MessageId(Guid.NewGuid());
        var sentAt = DateTimeOffset.UtcNow;
        var ct = CancellationToken.None;

        await writer.AddTextMessageAsync(conversationId, 1, "hello", messageId, sentAt, ct);
        await writer.AddTextMessageAsync(conversationId, 1, "hello", messageId, sentAt, ct);

        var count = await ctx.Messages.CountAsync(m => m.ConversationId == conversationId.Value && m.MessageGuid == messageId.Value);
        count.Should().Be(1);
    }

    [Test]
    public async Task AddTextMessage_infers_sender_as_other_participant_for_direct_conversation()
    {
        var ctx = CreateDbContext(out var conn);
        await using var _ = conn;
        var (conversationId, otherPeerId) = SeedConversation(ctx, 1);
        var writer = new SqliteChatMessageWriter(ctx);
        var messageId = new MessageId(Guid.NewGuid());
        var sentAt = DateTimeOffset.UtcNow;
        var ct = CancellationToken.None;

        await writer.AddTextMessageAsync(conversationId, 1, "hello", messageId, sentAt, ct);

        var msg = await ctx.Messages.SingleAsync(m => m.ConversationId == conversationId.Value && m.MessageGuid == messageId.Value);
        msg.SenderId.Should().Be(otherPeerId);
    }

    [Test]
    public async Task AddReadReceipt_is_idempotent_per_reader_and_message()
    {
        var ctx = CreateDbContext(out var conn);
        await using var _ = conn;
        var (conversationId, _) = SeedConversation(ctx, 1);
        var writer = new SqliteChatMessageWriter(ctx);
        var messageId = new MessageId(Guid.NewGuid());
        var sentAt = DateTimeOffset.UtcNow;
        var ct = CancellationToken.None;

        await writer.AddReadReceiptAsync(conversationId, 1, messageId, sentAt, ct);
        await writer.AddReadReceiptAsync(conversationId, 1, messageId, sentAt, ct);

        var count = await ctx.ReadReceipts.CountAsync(r => r.ConversationId == conversationId.Value && r.MessageGuid == messageId.Value);
        count.Should().Be(1);
    }

    [Test]
    public async Task AddEmojiAnnotation_is_idempotent_per_reactor_and_message_and_emoji()
    {
        var ctx = CreateDbContext(out var conn);
        await using var _ = conn;
        var (conversationId, _) = SeedConversation(ctx, 1);
        var writer = new SqliteChatMessageWriter(ctx);
        var messageId = new MessageId(Guid.NewGuid());
        var sentAt = DateTimeOffset.UtcNow;
        var ct = CancellationToken.None;

        await writer.AddEmojiAnnotationAsync(conversationId, 1, messageId, "👍", sentAt, ct);
        await writer.AddEmojiAnnotationAsync(conversationId, 1, messageId, "👍", sentAt, ct);
        await writer.AddEmojiAnnotationAsync(conversationId, 1, messageId, "🔥", sentAt, ct);

        var countThumbs = await ctx.EmojiReactions.CountAsync(e => e.ConversationId == conversationId.Value && e.MessageGuid == messageId.Value && e.Emoji == "👍");
        var countFire = await ctx.EmojiReactions.CountAsync(e => e.ConversationId == conversationId.Value && e.MessageGuid == messageId.Value && e.Emoji == "🔥");
        countThumbs.Should().Be(1);
        countFire.Should().Be(1);
    }
}
