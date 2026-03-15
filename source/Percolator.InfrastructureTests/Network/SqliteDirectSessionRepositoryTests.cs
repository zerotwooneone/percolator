using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Percolator.Infrastructure.Network;
using Percolator.Infrastructure.Persistence;
using Percolator.Infrastructure.Identity;
using Percolator.Network;
using Percolator.InfrastructureTests.Common;

namespace Percolator.InfrastructureTests.Network;

[TestFixture]
public class SqliteDirectSessionRepositoryTests
{
    private static PercolatorDbContext CreateDbContext(out SqliteConnection connection)
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<PercolatorDbContext>()
            .UseSqlite(connection)
            .Options;

        var ctx = TestDb.NewContext(options, 1);
        ctx.Database.EnsureCreated();
        // Seed default SelfIdentity required by repository scoping
        if (!ctx.SelfIdentities.Any())
        {
            ctx.SelfIdentities.Add(new SelfIdentityDbo { Id = 1, PeerId = Guid.NewGuid(), Name = "default" });
            ctx.SaveChanges();
        }
        return ctx;
    }

    [Test]
    public async Task GetByRemotePeerId_roundtrips()
    {
        var (ctx, dsr, peerId) = await CreateRepos();
        var sessionId = Guid.NewGuid();
        await dsr.UpsertAsync(peerId, new DirectSessionId(sessionId), 1);
        var ds = await dsr.GetByRemotePeerIdAsync(peerId, 1);
        ds.Should().NotBeNull();
        ds!.RemotePeerId.Value.Should().Be(peerId.Value);
        ds.SessionId.Value.Should().Be(sessionId);
    }

    [Test]
    public async Task DeleteByRemotePeerId_removes_mapping_and_is_idempotent()
    {
        var (ctx, dsr, peerId) = await CreateRepos();
        var sessionId = Guid.NewGuid();
        await dsr.UpsertAsync(peerId, new DirectSessionId(sessionId), 1);
        
        await dsr.DeleteByRemotePeerIdAsync(peerId, 1);
        (await dsr.GetByRemotePeerIdAsync(peerId, 1)).Should().BeNull();
        (await dsr.GetBySessionIdAsync(new DirectSessionId(sessionId), 1)).Should().BeNull();

        // Idempotent
        await dsr.DeleteByRemotePeerIdAsync(peerId, 1);
        (await dsr.GetByRemotePeerIdAsync(peerId, 1)).Should().BeNull();
    }

    [Test]
    public async Task Multiple_peers_have_isolated_sessions()
    {
        var ctx = CreateDbContext(out var _);
        var dsr = new SqliteDirectSessionRepository(ctx);

        var peerA = Percolator.Network.PeerId.NewId();
        var peerB = Percolator.Network.PeerId.NewId();

        ctx.PeerIdentities.Add(new PeerIdentityDbo { PeerId = peerA.Value, Name = "peer-a", Version = 0, CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow });
        ctx.PeerIdentities.Add(new PeerIdentityDbo { PeerId = peerB.Value, Name = "peer-b", Version = 0, CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow });
        await ctx.SaveChangesAsync();

        // Seed identity rows only; DirectSession repo doesn't require PeerConnection rows

        var sA = Guid.NewGuid();
        var sB = Guid.NewGuid();
        await dsr.UpsertAsync(peerA, new DirectSessionId(sA), 1);
        await dsr.UpsertAsync(peerB, new DirectSessionId(sB), 1);

        (await dsr.GetByRemotePeerIdAsync(peerA, 1))!.SessionId.Value.Should().Be(sA);
        (await dsr.GetByRemotePeerIdAsync(peerB, 1))!.SessionId.Value.Should().Be(sB);
        (await dsr.GetBySessionIdAsync(new DirectSessionId(sA), 1))!.RemotePeerId.Value.Should().Be(peerA.Value);
        (await dsr.GetBySessionIdAsync(new DirectSessionId(sB), 1))!.RemotePeerId.Value.Should().Be(peerB.Value);
    }
    private static async Task<(PercolatorDbContext Ctx, SqliteDirectSessionRepository Dsr, PeerId PeerId)> CreateRepos()
    {
        var ctx = CreateDbContext(out var _);
        var dsr = new SqliteDirectSessionRepository(ctx);

        // Ensure Peer and PeerConnection exist for FK
        var peerId = PeerId.NewId();
        ctx.PeerIdentities.Add(new PeerIdentityDbo { PeerId = peerId.Value, Name = "peer-a", Version = 0, CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow });
        await ctx.SaveChangesAsync();
        return (ctx, dsr, peerId);
    }

    [Test]
    public async Task GetBySessionId_returns_null_for_unknown()
    {
        var (ctx, dsr, _) = await CreateRepos();
        var unknown = Guid.NewGuid();
        var result = await dsr.GetBySessionIdAsync(new DirectSessionId(unknown), 1);
        result.Should().BeNull();
    }

    [Test]
    public async Task Upsert_and_GetBySessionId_roundtrips()
    {
        var (ctx, dsr, peerId) = await CreateRepos();
        var sessionId = Guid.NewGuid();
        await dsr.UpsertAsync(peerId, new DirectSessionId(sessionId), 1);
        var ds = await dsr.GetBySessionIdAsync(new DirectSessionId(sessionId), 1);
        ds.Should().NotBeNull();
        ds!.RemotePeerId.Value.Should().Be(peerId.Value);
        ds.SessionId.Value.Should().Be(sessionId);
    }

    [Test]
    public async Task Upsert_overwrites_for_same_peer()
    {
        var (ctx, dsr, peerId) = await CreateRepos();
        var s1 = Guid.NewGuid();
        var s2 = Guid.NewGuid();
        await dsr.UpsertAsync(peerId, new DirectSessionId(s1), 1);
        await dsr.UpsertAsync(peerId, new DirectSessionId(s2), 1);
        (await dsr.GetBySessionIdAsync(new DirectSessionId(s1), 1)).Should().BeNull();
        var ds = await dsr.GetBySessionIdAsync(new DirectSessionId(s2), 1);
        ds.Should().NotBeNull();
        ds!.RemotePeerId.Value.Should().Be(peerId.Value);
        ds.SessionId.Value.Should().Be(s2);
    }
}
