using FluentAssertions;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Percolator.Infrastructure.Network;
using Percolator.Infrastructure.Persistence;
using Percolator.Network;
using Percolator.Network.ValueObjects;

namespace Percolator.InfrastructureTests.Network;

[TestFixture]
public class SqlitePeerRoutingProfileRepositoryTests
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
    public async Task GetByPublicKeyAsync_roundtrips()
    {
        var ctx = CreateDbContext(out var _);
        var repo = new SqlitePeerRoutingProfileRepository(ctx);
        var peerId = PeerId.NewId();

        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var keyBytes = ecdsa.ExportSubjectPublicKeyInfo();
        var profile = new PeerRoutingProfile();
        profile.BindIdentity(peerId);
        profile.SetIdentityPublicKey(IdentityPublicKey.FromBytes(keyBytes));
        await repo.UpsertAsync(profile);

        var loaded = await repo.GetByPublicKeyAsync(IdentityPublicKey.FromBytes(keyBytes));
        loaded.Should().NotBeNull();
        loaded!.Id.Should().Be(peerId);
        loaded.IdentityPublicKey!.ToArray().Should().BeEquivalentTo(keyBytes);
    }

    [Test]
    public async Task GetStaleAsync_returns_profiles_with_old_reachability_change()
    {
        var ctx = CreateDbContext(out var _);
        var repo = new SqlitePeerRoutingProfileRepository(ctx);

        var freshId = PeerId.NewId();
        var staleId = PeerId.NewId();

        var fresh = new PeerRoutingProfile();
        fresh.BindIdentity(freshId);
        fresh.RecordReachability(ReachabilityStatus.Online, DateTimeOffset.UtcNow);
        await repo.UpsertAsync(fresh);

        var cutoff = DateTimeOffset.UtcNow.AddDays(-1);
        var stale = new PeerRoutingProfile();
        stale.BindIdentity(staleId);
        stale.RecordReachability(ReachabilityStatus.Offline, cutoff.AddMinutes(-5));
        await repo.UpsertAsync(stale);

        var results = await repo.GetStaleAsync(cutoff);
        results.Should().ContainSingle(p => p.Id == staleId);
        results.Should().NotContain(p => p.Id == freshId);
    }

    [Test]
    public async Task Upsert_and_GetById_roundtrips()
    {
        var ctx = CreateDbContext(out var _);
        var repo = new SqlitePeerRoutingProfileRepository(ctx);
        var peerId = PeerId.NewId();

        var profile = new PeerRoutingProfile();
        profile.BindIdentity(peerId);
        profile.RecordReachability(ReachabilityStatus.Online, DateTimeOffset.UtcNow);

        await repo.UpsertAsync(profile);

        var loaded = await repo.GetByIdAsync(peerId);
        loaded.Should().NotBeNull();
        loaded!.Id.Should().Be(peerId);
        loaded.Reachability.Status.Should().Be(ReachabilityStatus.Online);
    }
}
