using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Percolator.Infrastructure.Network;
using Percolator.Infrastructure.Persistence;
using Percolator.InfrastructureTests.Common;
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

        var ctx = TestDb.NewContextWithSchema(options, null);
        return ctx;
    }

    [Test]
    public async Task Add_refresh_prune_relays_persist_and_hydrate()
    {
        var ctx = CreateDbContext(out var _);
        var repo = new SqlitePeerRoutingProfileRepository(ctx);
        var pid = new PeerId(1);
        var prp = new PeerRoutingProfile();
        prp.BindIdentity(pid);

        var now = DateTimeOffset.UtcNow;
        var r1 = new PeerId(1);
        var r2 = new PeerId(1);
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
