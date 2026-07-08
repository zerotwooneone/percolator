using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Percolator.Chat.GroupMembership;
using Percolator.Chat.GroupLedger;
using Percolator.Chat.Messaging;
using Percolator.Chat.Messaging.ValueObjects;
using Percolator.Identity;
using Percolator.Infrastructure.Chat;
using Percolator.Infrastructure.Persistence;
using PublicIdentityId = Percolator.Identity.PublicIdentityId;
using Percolator.InfrastructureTests.Common;

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
        var ctx = TestDb.NewContextWithSchema(options, 1);
        if (!ctx.SelfIdentities.Any())
        {
            ctx.SelfIdentities.Add(new SelfIdentityDbo { Id = 1, PublicIdentityId = Guid.NewGuid(), Name = "default", DeviceId = new Percolator.Identity.DeviceId(1), ListeningPort = 5000, LastUsedUtc = DateTimeOffset.UtcNow });
            ctx.SaveChanges();
        }

        return ctx;
    }

    private static (ConversationId conversationId, uint otherPeerId, Percolator.Chat.GroupLedger.PublicIdentityId otherPublicIdentityId) SeedConversation(PercolatorDbContext ctx, SelfId selfIdentityId)
    {
        var self = ctx.SelfIdentities.Single(si => si.Id == selfIdentityId.Value);
        var conversationId = new ConversationId(Guid.NewGuid());
        var otherPeerId = 12345u;
        var otherPublicIdentityId = new Percolator.Chat.GroupLedger.PublicIdentityId(Guid.NewGuid());
        var fixedTime = new DateTimeOffset(2025, 1, 1, 12, 0, 0, TimeSpan.Zero);

        ctx.Conversations.Add(new ConversationDbo
        {
            Id = conversationId.Value,
            Name = "chat",
            SelfIdentityId = selfIdentityId.Value,
            CreatedAt = fixedTime,
            UpdatedAt = fixedTime
        });
        ctx.ConversationParticipants.Add(new ConversationParticipantDbo
        {
            ConversationId = conversationId.Value,
            ParticipantId = 1 // Self identity as participant
        });
        ctx.ConversationParticipants.Add(new ConversationParticipantDbo
        {
            ConversationId = conversationId.Value,
            ParticipantId = otherPeerId
        });
        ctx.SaveChanges();

        return (conversationId, otherPeerId, otherPublicIdentityId);
    }

    [Test]
    public async Task AddTextMessage_is_idempotent_per_message_id()
    {
        var ctx = CreateDbContext(out var conn);
        await using var _ = conn;
        var (conversationId, otherPeerId, otherPublicIdentityId) = SeedConversation(ctx, new SelfId(1));
        var writer = new SqliteChatMessageWriter(ctx);
        var messageId = new PublicMessageId(Guid.NewGuid());
        var sentAt = new DateTimeOffset(2025, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var ct = CancellationToken.None;
        var participantId = new RemoteParticipantId(otherPublicIdentityId, new ChatPeerId(otherPeerId));

        await writer.AddTextMessageAsync(conversationId, participantId, "hello", messageId, sentAt, ct);
        await writer.AddTextMessageAsync(conversationId, participantId, "hello", messageId, sentAt, ct);

        var count = await ctx.Messages.CountAsync(m => m.ConversationId == conversationId.Value && m.PublicMessageId == messageId.Value);
        count.Should().Be(1);
    }

    [Test]
    public async Task AddTextMessage_infers_sender_as_other_participant_for_direct_conversation()
    {
        var ctx = CreateDbContext(out var conn);
        await using var _ = conn;
        var (conversationId, otherPeerId, otherPublicIdentityId) = SeedConversation(ctx, new SelfId(1));
        var writer = new SqliteChatMessageWriter(ctx);
        var messageId = new PublicMessageId(Guid.NewGuid());
        var sentAt = new DateTimeOffset(2025, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var ct = CancellationToken.None;
        var participantId = new RemoteParticipantId(otherPublicIdentityId, new ChatPeerId(otherPeerId));

        await writer.AddTextMessageAsync(conversationId, participantId, "hello", messageId, sentAt, ct);

        var msg = await ctx.Messages.SingleAsync(m => m.ConversationId == conversationId.Value && m.PublicMessageId == messageId.Value);
        msg.SenderPeerId.Should().Be(otherPeerId);
    }

    [Test]
    public async Task AddReadReceipt_is_idempotent_per_reader_and_message()
    {
        var ctx = CreateDbContext(out var conn);
        await using var _ = conn;
        var (conversationId, otherPeerId, otherPublicIdentityId) = SeedConversation(ctx, new SelfId(1));
        var writer = new SqliteChatMessageWriter(ctx);
        var messageId = new PublicMessageId(Guid.NewGuid());
        var sentAt = new DateTimeOffset(2025, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var ct = CancellationToken.None;
        var senderId = new RemoteParticipantId(otherPublicIdentityId, new ChatPeerId(otherPeerId));
        var readerId = new ChatPeerId(otherPeerId);

        // Add message first
        await writer.AddTextMessageAsync(conversationId, senderId, "test", messageId, sentAt, ct);

        await writer.AddReadReceiptAsync(conversationId, new ChatSelfId(1), readerId, messageId, sentAt, ct);
        await writer.AddReadReceiptAsync(conversationId, new ChatSelfId(1), readerId, messageId, sentAt, ct);

        var count = await ctx.ReadReceipts.CountAsync(r => r.ConversationId == conversationId.Value && r.MessageGuid == messageId.Value);
        count.Should().Be(1);
    }
}
