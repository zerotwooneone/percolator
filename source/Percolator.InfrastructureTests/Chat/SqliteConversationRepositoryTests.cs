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
        return ctx;
    }

    private static Conversation NewConversation()
    {
        var id = ConversationId.NewId();
        var channelId = new ChannelId(Guid.NewGuid().ToByteArray());
        var p1 = ParticipantId.NewId();
        var p2 = ParticipantId.NewId();
        var conv = new Conversation(id, channelId, new[] { p1, p2 }, Enumerable.Empty<Message>(), "chat");
        conv.AddMessage(p1, "hello");
        conv.AddMessage(p2, "world");
        return conv;
    }

    [Test]
    public async Task Add_and_GetById_roundtrips_full_graph()
    {
        var ctx = CreateDbContext(out _);
        var repo = new SqliteConversationRepository(ctx);
        var c = NewConversation();

        await repo.AddAsync(c);
        var loaded = await repo.GetByIdAsync(c.Id);

        loaded.Should().NotBeNull();
        loaded!.Id.Value.Should().Be(c.Id.Value);
        loaded.ChannelId.Value.Should().BeEquivalentTo(c.ChannelId.Value);
        loaded.Name.Should().Be(c.Name);
        loaded.Participants.Select(p => p.Value).Should().BeEquivalentTo(c.Participants.Select(p => p.Value));
        loaded.Messages.Select(m => (m.Id.Value, m.SenderId.Value, m.Content)).Should()
            .BeEquivalentTo(c.Messages.Select(m => (m.Id.Value, m.SenderId.Value, m.Content)));
    }

    [Test]
    public async Task GetByChannelId_returns_conversation()
    {
        var ctx = CreateDbContext(out _);
        var repo = new SqliteConversationRepository(ctx);
        var c = NewConversation();

        await repo.AddAsync(c);
        var loaded = await repo.GetByChannelIdAsync(c.ChannelId);

        loaded.Should().NotBeNull();
        loaded!.Id.Value.Should().Be(c.Id.Value);
    }

    [Test]
    public async Task Update_replaces_participants_and_messages()
    {
        var ctx = CreateDbContext(out _);
        var repo = new SqliteConversationRepository(ctx);
        var c = NewConversation();
        await repo.AddAsync(c);

        // Create an updated conversation with different participants and messages
        var p3 = ParticipantId.NewId();
        var p4 = ParticipantId.NewId();
        var updated = new Conversation(c.Id, c.ChannelId, new[] { p3, p4 }, Enumerable.Empty<Message>(), "updated");
        updated.AddMessage(p3, "new1");
        updated.AddMessage(p4, "new2");

        await repo.UpdateAsync(updated);
        var loaded = await repo.GetByIdAsync(c.Id);

        loaded!.Name.Should().Be("updated");
        loaded.Participants.Select(p => p.Value).Should().BeEquivalentTo(new[] { p3.Value, p4.Value });
        loaded.Messages.Select(m => m.Content).Should().BeEquivalentTo(new[] { "new1", "new2" });
    }

    [Test]
    public async Task Unique_ChannelId_is_enforced()
    {
        var ctx = CreateDbContext(out _);
        var repo = new SqliteConversationRepository(ctx);
        var c1 = NewConversation();
        var c2 = new Conversation(ConversationId.NewId(), c1.ChannelId, c1.Participants, c1.Messages, "dup");

        await repo.AddAsync(c1);
        Func<Task> act = async () => await repo.AddAsync(c2);
        await act.Should().ThrowAsync<InvalidOperationException>();
    }
}
