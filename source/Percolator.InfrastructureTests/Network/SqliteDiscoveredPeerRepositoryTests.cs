using System.Net;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Percolator.Infrastructure.Network;
using Percolator.Infrastructure.Persistence;
using Percolator.Network;
using Percolator.Network.ValueObjects;

namespace Percolator.InfrastructureTests.Network;

[TestFixture]
public class SqliteDiscoveredPeerRepositoryTests
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

    [Test]
    public async Task Upsert_and_GetByDiscoveryKey_roundtrips()
    {
        var ctx = CreateDbContext(out var _);
        var repo = new SqliteDiscoveredPeerRepository(ctx);

        var key = new DiscoveryKey("seed:localhost:5001");
        var pkh = new PublicKeyHash(new byte[32]);
        var now = DateTimeOffset.UtcNow;
        var dp = DiscoveredPeer.Create(key, pkh, now);
        dp.ObserveEndpoint(new GrpcEndPoint(new DnsEndPoint("localhost", 5001), now), now);

        await repo.UpsertAsync(dp);
        var loaded = await repo.GetByDiscoveryKeyAsync(key);

        loaded.Should().NotBeNull();
        loaded!.DiscoveryKey.Should().Be(key);
        loaded.PublicKeyHash.Should().Be(pkh);
    }

    [Test]
    public async Task PromoteToRoutingProfile_creates_profile_and_marks_promoted()
    {
        var ctx = CreateDbContext(out var _);
        var repo = new SqliteDiscoveredPeerRepository(ctx);

        var key = new DiscoveryKey("seed:node:6002");
        var now = DateTimeOffset.UtcNow;
        var dp = DiscoveredPeer.Create(key, null, now);
        dp.ObserveEndpoint(new GrpcEndPoint(new DnsEndPoint("node", 6002), now), now);

        var peerId = PeerId.NewId();
        var prp = await repo.PromoteToRoutingProfileAsync(dp, peerId);
        prp.Id.Should().Be(peerId);
    }

    [Test]
    public async Task GetByPublicKeyHashAsync_roundtrips()
    {
        var ctx = CreateDbContext(out var _);
        var repo = new SqliteDiscoveredPeerRepository(ctx);

        var key = new DiscoveryKey("seed:pkh:6003");
        var pkh = new PublicKeyHash(new byte[] { 9, 9, 9 });
        var now = DateTimeOffset.UtcNow;
        var dp = DiscoveredPeer.Create(key, pkh, now);
        await repo.UpsertAsync(dp);

        var loaded = await repo.GetByPublicKeyHashAsync(pkh);
        loaded.Should().NotBeNull();
        loaded!.PublicKeyHash!.Value.Should().BeEquivalentTo(pkh.Value);
    }

    [Test]
    public async Task GetCandidatesAsync_orders_by_last_seen_then_confidence()
    {
        var ctx = CreateDbContext(out var _);
        var repo = new SqliteDiscoveredPeerRepository(ctx);
        var seenSince = DateTimeOffset.UtcNow.AddHours(-1);

        var a = DiscoveredPeer.Create(new DiscoveryKey("seed:a"), new PublicKeyHash(new byte[] { 1 }), seenSince.AddMinutes(-10));
        a.RecordDiscovery(DiscoverySource.Dht, seenSince.AddMinutes(10)); // lastSeen  +10
        var b = DiscoveredPeer.Create(new DiscoveryKey("seed:b"), new PublicKeyHash(new byte[] { 2 }), seenSince.AddMinutes(-10));
        b.RecordDiscovery(DiscoverySource.Dht, seenSince.AddMinutes(5)); // lastSeen  +5
        // Boost b confidence slightly
        b.RecordDiscovery(DiscoverySource.Dht, seenSince.AddMinutes(6));
        var c = DiscoveredPeer.Create(new DiscoveryKey("seed:c"), new PublicKeyHash(new byte[] { 3 }), seenSince.AddMinutes(-10));
        c.RecordDiscovery(DiscoverySource.Dht, seenSince.AddMinutes(10)); // tie on lastSeen with a, but lower confidence

        await repo.UpsertAsync(a);
        await repo.UpsertAsync(b);
        await repo.UpsertAsync(c);

        var candidates = (await repo.GetCandidatesAsync(seenSince)).ToList();
        candidates.Should().HaveCount(3);
        // a and c share same lastSeen; a has higher confidence than c, so a before c; b last
        candidates[0].DiscoveryKey.Value.Should().Be("seed:a");
        candidates[1].DiscoveryKey.Value.Should().Be("seed:c");
        candidates[2].DiscoveryKey.Value.Should().Be("seed:b");
    }

    [Test]
    public async Task BindIdentityAsync_binds_peer_id_on_profile()
    {
        var ctx = CreateDbContext(out var _);
        var repo = new SqliteDiscoveredPeerRepository(ctx);
        var prp = new PeerRoutingProfile();
        var pid = PeerId.NewId();
        var bound = await repo.BindIdentityAsync(prp, pid);
        bound.Id.Should().Be(pid);
    }

    [Test]
    public async Task Upsert_merges_and_GetByDiscoveryKey_hydrates_endpoints_with_last_seen()
    {
        var ctx = CreateDbContext(out var _);
        var repo = new SqliteDiscoveredPeerRepository(ctx);
        var key = new DiscoveryKey("seed:endpoints:7001");
        var now = DateTimeOffset.UtcNow;
        var dp = DiscoveredPeer.Create(key, null, now);
        var ep1t0 = now.AddMinutes(-5);
        dp.ObserveEndpoint(new GrpcEndPoint(new DnsEndPoint("h1", 7001), ep1t0), ep1t0);
        await repo.UpsertAsync(dp);

        // Refresh endpoint with newer last-seen and add second endpoint
        var ep1t1 = now.AddMinutes(5);
        dp.ObserveEndpoint(new GrpcEndPoint(new DnsEndPoint("h1", 7001), ep1t1), ep1t1);
        var ep2t = now.AddMinutes(3);
        dp.ObserveEndpoint(new GrpcEndPoint(new DnsEndPoint("h2", 7002), ep2t), ep2t);
        await repo.UpsertAsync(dp);

        var loaded = await repo.GetByDiscoveryKeyAsync(key);
        loaded!.Endpoints.Should().ContainSingle(e => e.EndPoint.Host == "h1" && e.EndPoint.Port == 7001 && e.LastSeen == ep1t1);
        loaded.Endpoints.Should().ContainSingle(e => e.EndPoint.Host == "h2" && e.EndPoint.Port == 7002 && e.LastSeen == ep2t);
    }
}
