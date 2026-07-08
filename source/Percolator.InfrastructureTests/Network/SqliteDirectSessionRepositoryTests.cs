using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Percolator.Identity;
using Percolator.Identity.Model;
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

        var ctx = TestDb.NewContextWithSchema(options, 1);

        // Seed default SelfIdentity required by repository scoping
        if (!ctx.SelfIdentities.Any())
        {
            ctx.SelfIdentities.Add(new SelfIdentityDbo { Id = 1, PublicIdentityId = Guid.NewGuid(), Name = "default", DeviceId = 1, ListeningPort = 5000, LastUsedUtc = DateTimeOffset.UtcNow });
            ctx.SaveChanges();
        }
        return ctx;
    }

    [Test]
    public async Task GetByRemotePeerId_roundtrips()
    {
        var (ctx, dsr, peerId) = await CreateRepos();
        var sessionId = Guid.NewGuid();
        await dsr.UpsertAsync(peerId, new DirectSessionId(sessionId), new NetworkSelfId(1));
        var ds = await dsr.GetByRemotePeerIdAsync(peerId, new NetworkSelfId(1));
        ds.Should().NotBeNull();
        ds!.RemotePeerId.Should().Be(peerId);
        ds.SessionId.Value.Should().Be(sessionId);
    }

    [Test]
    public async Task DeleteByRemotePeerId_removes_mapping_and_is_idempotent()
    {
        var (ctx, dsr, peerId) = await CreateRepos();
        var sessionId = Guid.NewGuid();
        await dsr.UpsertAsync(peerId, new DirectSessionId(sessionId), new NetworkSelfId(1));
        
        await dsr.DeleteByRemotePeerIdAsync(peerId, new NetworkSelfId(1));
        (await dsr.GetByRemotePeerIdAsync(peerId, new NetworkSelfId(1))).Should().BeNull();
        (await dsr.GetBySessionIdAsync(new DirectSessionId(sessionId), new NetworkSelfId(1))).Should().BeNull();

        // Idempotent
        await dsr.DeleteByRemotePeerIdAsync(peerId, new NetworkSelfId(1));
        (await dsr.GetByRemotePeerIdAsync(peerId, new NetworkSelfId(1))).Should().BeNull();
    }

    [Test]
    public async Task Multiple_peers_have_isolated_sessions()
    {
        var ctx = CreateDbContext(out var _);
        var dsr = new SqliteDirectSessionRepository(ctx);

        var peerA = new Percolator.Network.PeerId(12345);
        var peerB = new Percolator.Network.PeerId(67890);
        var fixedTime = new DateTimeOffset(2025, 1, 1, 12, 0, 0, TimeSpan.Zero);

        ctx.PeerIdentities.Add(new PeerIdentityDbo { PeerId = peerA.Value, PublicIdentityId = Guid.NewGuid(), Name = "peer-a", Version = 0, CreatedAtUtc = fixedTime, UpdatedAtUtc = fixedTime });
        ctx.PeerIdentities.Add(new PeerIdentityDbo { PeerId = peerB.Value, PublicIdentityId = Guid.NewGuid(), Name = "peer-b", Version = 0, CreatedAtUtc = fixedTime, UpdatedAtUtc = fixedTime });
        await ctx.SaveChangesAsync();

        // Seed identity rows only; DirectSession repo doesn't require PeerConnection rows

        var sA = Guid.NewGuid();
        var sB = Guid.NewGuid();
        await dsr.UpsertAsync(peerA, new DirectSessionId(sA), new NetworkSelfId(1));
        await dsr.UpsertAsync(peerB, new DirectSessionId(sB), new NetworkSelfId(1));

        (await dsr.GetByRemotePeerIdAsync(peerA, new NetworkSelfId(1)))!.SessionId.Value.Should().Be(sA);
        (await dsr.GetByRemotePeerIdAsync(peerB, new NetworkSelfId(1)))!.SessionId.Value.Should().Be(sB);
        (await dsr.GetBySessionIdAsync(new DirectSessionId(sA), new NetworkSelfId(1)))!.RemotePeerId.Should().Be(peerA);
        (await dsr.GetBySessionIdAsync(new DirectSessionId(sB), new NetworkSelfId(1)))!.RemotePeerId.Should().Be(peerB);
    }
    private static async Task<(PercolatorDbContext Ctx, SqliteDirectSessionRepository Dsr, Percolator.Network.PeerId PeerId)> CreateRepos()
    {
        var ctx = CreateDbContext(out var _);
        var dsr = new SqliteDirectSessionRepository(ctx);

        // Ensure Peer and PeerConnection exist for FK
        var peerId = new Percolator.Network.PeerId(54321);
        var fixedTime = new DateTimeOffset(2025, 1, 1, 12, 0, 0, TimeSpan.Zero);
        ctx.PeerIdentities.Add(new PeerIdentityDbo { PeerId = peerId.Value, PublicIdentityId = Guid.NewGuid(), Name = "peer-a", Version = 0, CreatedAtUtc = fixedTime, UpdatedAtUtc = fixedTime });
        await ctx.SaveChangesAsync();
        return (ctx, dsr, peerId);
    }

    [Test]
    public async Task GetBySessionId_returns_null_for_unknown()
    {
        var (ctx, dsr, _) = await CreateRepos();
        var unknown = Guid.NewGuid();
        var result = await dsr.GetBySessionIdAsync(new DirectSessionId(unknown), new NetworkSelfId(1));
        result.Should().BeNull();
    }

    [Test]
    public async Task Upsert_and_GetBySessionId_roundtrips()
    {
        var (ctx, dsr, peerId) = await CreateRepos();
        var sessionId = Guid.NewGuid();
        await dsr.UpsertAsync(peerId, new DirectSessionId(sessionId), new NetworkSelfId(1));
        var ds = await dsr.GetBySessionIdAsync(new DirectSessionId(sessionId), new NetworkSelfId(1));
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
        await dsr.UpsertAsync(peerId, new DirectSessionId(s1), new NetworkSelfId(1));
        await dsr.UpsertAsync(peerId, new DirectSessionId(s2), new NetworkSelfId(1));
        (await dsr.GetBySessionIdAsync(new DirectSessionId(s1), new NetworkSelfId(1))).Should().BeNull();
        var ds = await dsr.GetBySessionIdAsync(new DirectSessionId(s2), new NetworkSelfId(1));
        ds.Should().NotBeNull();
        ds!.RemotePeerId.Value.Should().Be(peerId.Value);
        ds.SessionId.Value.Should().Be(s2);
    }
}
