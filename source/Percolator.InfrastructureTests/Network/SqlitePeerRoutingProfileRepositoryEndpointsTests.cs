using System.Net;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Percolator.Infrastructure.Network;
using Percolator.Infrastructure.Persistence;
using Percolator.Network;

namespace Percolator.InfrastructureTests.Network;

[TestFixture]
public class SqlitePeerRoutingProfileRepositoryEndpointsTests
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
    public async Task Upsert_merges_and_GetById_hydrates_endpoints_with_last_seen()
    {
        var ctx = CreateDbContext(out var _);
        var repo = new SqlitePeerRoutingProfileRepository(ctx);
        var pid = new PeerId(1);
        var now = DateTimeOffset.UtcNow;

        var prp = new PeerRoutingProfile();
        prp.BindIdentity(pid);
        var t0 = now.AddMinutes(-5);
        prp.AddGrpcEndPoint(new GrpcEndPoint(new DnsEndPoint("e1", 8001), t0), t0);
        await repo.UpsertAsync(prp);

        // refresh e1 and add e2
        var t1 = now.AddMinutes(5);
        prp.AddGrpcEndPoint(new GrpcEndPoint(new DnsEndPoint("e1", 8001), t1), t1);
        var t2 = now.AddMinutes(3);
        prp.AddGrpcEndPoint(new GrpcEndPoint(new DnsEndPoint("e2", 8002), t2), t2);
        await repo.UpsertAsync(prp);

        var loaded = await repo.GetByIdAsync(pid);
        loaded!.Endpoints.Should().ContainSingle(e => e.EndPoint.Host == "e1" && e.EndPoint.Port == 8001 && e.LastSeen == t1);
        loaded.Endpoints.Should().ContainSingle(e => e.EndPoint.Host == "e2" && e.EndPoint.Port == 8002 && e.LastSeen == t2);
    }
}
