using System;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;
using Percolator.Chat;
using Percolator.Chat.ValueObjects;
using Percolator.Infrastructure.Chat;
using Percolator.Infrastructure.Persistence;

namespace Percolator.InfrastructureTests.Chat;

[TestFixture]
public class SqliteConversationRepositoryTests
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
        // Seed default SelfIdentity required by repository scoping
        if (!ctx.SelfIdentities.Any())
        {
            ctx.SelfIdentities.Add(new SelfIdentityDbo { Id = 1, PeerId = Guid.NewGuid(), Name = "default" });
            ctx.SaveChanges();
        }

        return ctx;
    }

    private static Conversation NewConversation()
    {
        var id = ConversationId.NewId();
        var p1 = ParticipantId.NewId();
        var p2 = ParticipantId.NewId();
        var conv = new Conversation(id, new[] { p1, p2 }, Enumerable.Empty<Message>(), "chat");
        conv.AddMessage(p1, "hello");
        conv.AddMessage(p2, "world");
        return conv;
    }

    [Test]
    public async Task GetByGroupGuidAsync_returns_group_conversation()
    {
        var ctx = CreateDbContext(out _);
        var repo = new SqliteConversationRepository(ctx);

        var groupGuid = Guid.NewGuid();
        var convId = Guid.NewGuid();
        var self = ctx.SelfIdentities.Single(si => si.Id == 1);

        ctx.Conversations.Add(new ConversationDbo
        {
            Id = convId,
            Name = "group",
            SelfIdentityId = 1,
            GroupConversationGuid = groupGuid,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        ctx.ConversationParticipants.Add(new ConversationParticipantDbo { ConversationId = convId, ParticipantId = self.PeerId });
        ctx.ConversationParticipants.Add(new ConversationParticipantDbo { ConversationId = convId, ParticipantId = Guid.NewGuid() });
        ctx.SaveChanges();

        var loaded = await repo.GetByGroupGuidAsync(groupGuid, 1);
        loaded.Should().NotBeNull();
        loaded!.Id.Value.Should().Be(convId);
    }

    [Test]
    public async Task GetByGroupGuidAsync_returns_null_for_nonexistent_group_guid()
    {
        var ctx = CreateDbContext(out _);
        var repo = new SqliteConversationRepository(ctx);

        var groupGuid = Guid.NewGuid();

        var loaded = await repo.GetByGroupGuidAsync(groupGuid, 1);
        loaded.Should().BeNull();
    }

    [Test]
    public async Task GetByParticipantPairAsync_returns_direct_conversation()
    {
        var ctx = CreateDbContext(out _);
        var repo = new SqliteConversationRepository(ctx);

        var convId = Guid.NewGuid();
        var self = ctx.SelfIdentities.Single(si => si.Id == 1);
        var other = Guid.NewGuid();

        ctx.Conversations.Add(new ConversationDbo
        {
            Id = convId,
            Name = "direct",
            SelfIdentityId = 1,
            GroupConversationGuid = null,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        ctx.ConversationParticipants.Add(new ConversationParticipantDbo { ConversationId = convId, ParticipantId = self.PeerId });
        ctx.ConversationParticipants.Add(new ConversationParticipantDbo { ConversationId = convId, ParticipantId = other });
        ctx.SaveChanges();

        var loaded = await repo.GetByParticipantPairAsync(1, other);
        loaded.Should().NotBeNull();
        loaded!.Id.Value.Should().Be(convId);
    }

    [Test]
    public async Task GetByParticipantPairAsync_returns_null_for_nonexistent_participant_pair()
    {
        var ctx = CreateDbContext(out _);
        var repo = new SqliteConversationRepository(ctx);

        var other = Guid.NewGuid();

        var loaded = await repo.GetByParticipantPairAsync(1, other);
        loaded.Should().BeNull();
    }

    [Test]
    public async Task UpsertDirectSessionMappingAsync_inserts_and_updates_mapping()
    {
        var ctx = CreateDbContext(out _);
        var repo = new SqliteConversationRepository(ctx);

        var convId = Guid.NewGuid();
        ctx.Conversations.Add(new ConversationDbo
        {
            Id = convId,
            Name = "direct",
            SelfIdentityId = 1,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        ctx.SaveChanges();

        var sessionId = Guid.NewGuid();
        await repo.UpsertDirectSessionMappingAsync(1, sessionId, new ConversationId(convId));

        var mapping = await ctx.DirectSessionConversations.AsNoTracking().SingleAsync(m => m.SelfIdentityId == 1 && m.DirectSessionId == sessionId);
        mapping.ConversationId.Should().Be(convId);

        var newConvId = Guid.NewGuid();
        ctx.Conversations.Add(new ConversationDbo
        {
            Id = newConvId,
            Name = "direct2",
            SelfIdentityId = 1,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        ctx.SaveChanges();

        await repo.UpsertDirectSessionMappingAsync(1, sessionId, new ConversationId(newConvId));
        var updated = await ctx.DirectSessionConversations.AsNoTracking().SingleAsync(m => m.SelfIdentityId == 1 && m.DirectSessionId == sessionId);
        updated.ConversationId.Should().Be(newConvId);
    }

    [Test]
    public async Task UpsertDirectSessionMappingAsync_inserts_mapping_for_nonexistent_session_id()
    {
        var ctx = CreateDbContext(out _);
        var repo = new SqliteConversationRepository(ctx);

        var convId = Guid.NewGuid();
        ctx.Conversations.Add(new ConversationDbo
        {
            Id = convId,
            Name = "direct",
            SelfIdentityId = 1,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        ctx.SaveChanges();

        var sessionId = Guid.NewGuid();
        await repo.UpsertDirectSessionMappingAsync(1, sessionId, new ConversationId(convId));

        var mapping = await ctx.DirectSessionConversations.AsNoTracking().SingleAsync(m => m.SelfIdentityId == 1 && m.DirectSessionId == sessionId);
        mapping.ConversationId.Should().Be(convId);
    }

    [Test]
    public async Task Add_and_GetById_roundtrips_full_graph()
    {
        var ctx = CreateDbContext(out _);
        var repo = new SqliteConversationRepository(ctx);
        var c = NewConversation();

        await repo.AddAsync(c, 1);
        var loaded = await repo.GetByIdAsync(c.Id, 1);

        loaded.Should().NotBeNull();
        loaded!.Id.Value.Should().Be(c.Id.Value);
        // ChannelId removed from domain; ensure other fields match
        loaded.Name.Should().Be(c.Name);
        loaded.Participants.Select(p => p.Value).Should().BeEquivalentTo(c.Participants.Select(p => p.Value));
        loaded.Messages.Select(m => (m.Id.Value, m.SenderId.Value, m.Content)).Should()
            .BeEquivalentTo(c.Messages.Select(m => (m.Id.Value, m.SenderId.Value, m.Content)));
    }

    [Test]
    public async Task Update_replaces_participants_and_messages()
    {
        var ctx = CreateDbContext(out _);
        var repo = new SqliteConversationRepository(ctx);
        var c = NewConversation();
        await repo.AddAsync(c, 1);

        // Create an updated conversation with different participants and messages
        var p3 = ParticipantId.NewId();
        var p4 = ParticipantId.NewId();
        var updated = new Conversation(c.Id, new[] { p3, p4 }, Enumerable.Empty<Message>(), "updated");
        updated.AddMessage(p3, "new1");
        updated.AddMessage(p4, "new2");

        await repo.UpdateAsync(updated, 1);
        var loaded = await repo.GetByIdAsync(c.Id, 1);

        loaded!.Name.Should().Be("updated");
        loaded.Participants.Select(p => p.Value).Should().BeEquivalentTo(new[] { p3.Value, p4.Value });
        loaded.Messages.Select(m => m.Content).Should().BeEquivalentTo(new[] { "new1", "new2" });
    }
}
