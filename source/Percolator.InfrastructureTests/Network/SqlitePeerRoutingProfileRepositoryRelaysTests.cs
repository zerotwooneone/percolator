using System;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;
using Percolator.Infrastructure.Network;
using Percolator.Infrastructure.Persistence;
using Percolator.Network;

namespace Percolator.InfrastructureTests.Network;

[TestFixture]
public class SqlitePeerRoutingProfileRepositoryRelaysTests
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
    public async Task Add_refresh_prune_relays_persist_and_hydrate()
    {
        var ctx = CreateDbContext(out var _);
        var repo = new SqlitePeerRoutingProfileRepository(ctx);
        var pid = PeerId.NewId();
        var prp = new PeerRoutingProfile();
        prp.BindIdentity(pid);

        var now = DateTimeOffset.UtcNow;
        var r1 = PeerId.NewId();
        var r2 = PeerId.NewId();
        var t1 = now.AddMinutes(-10);
        var t2 = now.AddMinutes(-5);
        prp.AddOrRefreshRelay(r1, t1);
        prp.AddOrRefreshRelay(r2, t2);
        await repo.UpsertAsync(prp);

        // refresh r1 newer, prune r2
        var t1New = now.AddMinutes(1);
        prp.AddOrRefreshRelay(r1, t1New);
        var cutoff = now.AddMinutes(-1);
        prp.PruneStaleRelays(cutoff);
        await repo.UpsertAsync(prp);

        var loaded = await repo.GetByIdAsync(pid);
        loaded.Should().NotBeNull();
        loaded!.Relays.Should().ContainSingle(x => x.RelayPeerId == r1 && x.Freshness.LastSeenUtc == t1New);
    }
}
