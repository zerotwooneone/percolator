using System;
using System.Security.Cryptography;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;
using Percolator.Infrastructure.Network;
using Percolator.Infrastructure.Persistence;
using Percolator.Infrastructure.Identity;
using Percolator.Network;

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

    [Test]
    public async Task GetByRemotePeerId_roundtrips()
    {
        var (ctx, _, dsr, peerId) = await CreateRepos();
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
        var (ctx, _, dsr, peerId) = await CreateRepos();
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
        var pcr = new SqlitePeerConnectionRepository(ctx);
        var dsr = new SqliteDirectSessionRepository(ctx);

        var peerA = Percolator.Network.PeerId.NewId();
        var peerB = Percolator.Network.PeerId.NewId();

        ctx.PeerIdentities.Add(new PeerIdentityDbo { PeerId = peerA.Value, Name = "peer-a", Version = 0, CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow });
        ctx.PeerIdentities.Add(new PeerIdentityDbo { PeerId = peerB.Value, Name = "peer-b", Version = 0, CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow });
        await ctx.SaveChangesAsync();

        await pcr.SaveAsync(new PeerConnection(peerA, new DirectMessagePublicKey(RandomNumberGenerator.GetBytes(32)), Array.Empty<GrpcEndPoint>(), Array.Empty<TlsCertificate>(), DateTimeOffset.UtcNow));
        await pcr.SaveAsync(new PeerConnection(peerB, new DirectMessagePublicKey(RandomNumberGenerator.GetBytes(32)), Array.Empty<GrpcEndPoint>(), Array.Empty<TlsCertificate>(), DateTimeOffset.UtcNow));

        var sA = Guid.NewGuid();
        var sB = Guid.NewGuid();
        await dsr.UpsertAsync(peerA, new DirectSessionId(sA), 1);
        await dsr.UpsertAsync(peerB, new DirectSessionId(sB), 1);

        (await dsr.GetByRemotePeerIdAsync(peerA, 1))!.SessionId.Value.Should().Be(sA);
        (await dsr.GetByRemotePeerIdAsync(peerB, 1))!.SessionId.Value.Should().Be(sB);
        (await dsr.GetBySessionIdAsync(new DirectSessionId(sA), 1))!.RemotePeerId.Value.Should().Be(peerA.Value);
        (await dsr.GetBySessionIdAsync(new DirectSessionId(sB), 1))!.RemotePeerId.Value.Should().Be(peerB.Value);
    }
    private static async Task<(PercolatorDbContext Ctx, SqlitePeerConnectionRepository Pcr, SqliteDirectSessionRepository Dsr, PeerId PeerId)> CreateRepos()
    {
        var ctx = CreateDbContext(out var _);
        var pcr = new SqlitePeerConnectionRepository(ctx);
        var dsr = new SqliteDirectSessionRepository(ctx);

        // Ensure Peer and PeerConnection exist for FK
        var peerId = PeerId.NewId();
        ctx.PeerIdentities.Add(new PeerIdentityDbo { PeerId = peerId.Value, Name = "peer-a", Version = 0, CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow });
        await ctx.SaveChangesAsync();
        var now = DateTimeOffset.UtcNow;
        await pcr.SaveAsync(new PeerConnection(peerId, new DirectMessagePublicKey(RandomNumberGenerator.GetBytes(32)), Array.Empty<GrpcEndPoint>(), Array.Empty<TlsCertificate>(), now));
        return (ctx, pcr, dsr, peerId);
    }

    [Test]
    public async Task GetBySessionId_returns_null_for_unknown()
    {
        var (ctx, _, dsr, _) = await CreateRepos();
        var unknown = Guid.NewGuid();
        var result = await dsr.GetBySessionIdAsync(new DirectSessionId(unknown), 1);
        result.Should().BeNull();
    }

    [Test]
    public async Task Upsert_and_GetBySessionId_roundtrips()
    {
        var (ctx, _, dsr, peerId) = await CreateRepos();
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
        var (ctx, _, dsr, peerId) = await CreateRepos();
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

    [Test]
    public async Task Cascade_delete_removes_direct_session()
    {
        var (ctx, _, dsr, peerId) = await CreateRepos();
        var s = Guid.NewGuid();
        await dsr.UpsertAsync(peerId, new DirectSessionId(s), 1);
        // Delete peer identity (which cascades to PeerConnection and should cascade to DirectSession as well)
        var peer = await ctx.PeerIdentities.FindAsync(peerId.Value);
        ctx.PeerIdentities.Remove(peer!);
        await ctx.SaveChangesAsync();
        var result = await dsr.GetBySessionIdAsync(new DirectSessionId(s), 1);
        result.Should().BeNull();
    }
}
