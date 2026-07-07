using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Percolator.Identity;
using Percolator.Identity.Model;
using Percolator.Infrastructure.Persistence;
using Percolator.Infrastructure.Repositories;
using Percolator.InfrastructureTests.Common;

namespace Percolator.InfrastructureTests.Identity;

[TestFixture]
public class SqlitePeerIdentityRepositoryTests
{
    private static PercolatorDbContext CreateContext(SqliteConnection conn)
    {
        var options = new DbContextOptionsBuilder<PercolatorDbContext>()
            .UseSqlite(conn)
            .Options;
        var db = TestDb.NewContextWithSchema(options, null);
        return db;
    }

    private static byte[] Bytes(params byte[] b) => b;

    [Test]
    public async Task Save_and_get_roundtrip()
    {
        using var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        await using var db = CreateContext(conn);
        var repo = new SqlitePeerIdentityRepository(db);

        var now = new DateTimeOffset(2025, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var aggregate = new PeerIdentity(new PeerId(1), new PublicIdentityId(Guid.NewGuid()));
        aggregate.SetDisplayName("Alice");
        aggregate.AddKey(Bytes(1,2,3), now, now.AddDays(1), now);

        // Act
        Assert.DoesNotThrowAsync(async () => await repo.SaveAsync(aggregate));
        var loaded = await repo.GetByIdAsync(aggregate.Id);

        // Assert (RED for now)
        Assert.That(loaded, Is.Not.Null);
        Assert.That(loaded!.Id, Is.EqualTo(aggregate.Id));
        Assert.That(loaded.DisplayName!.Value, Is.EqualTo("Alice"));
        Assert.That(loaded.GetActiveKey(now)!.Fingerprint, Is.Not.Null);
    }

    [Test]
    public async Task Find_by_fingerprint_returns_identity()
    {
        using var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        await using var db = CreateContext(conn);
        var repo = new SqlitePeerIdentityRepository(db);

        var now = new DateTimeOffset(2025, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var aggregate = new PeerIdentity(new PeerId(2), new PublicIdentityId(Guid.NewGuid()));
        aggregate.AddKey(Bytes(9,9,9), now, now.AddDays(1), now);
        await repo.SaveAsync(aggregate);

        var fp = aggregate.GetActiveKey(now)!.Fingerprint;
        var loaded = await repo.FindByPublicKeyHashAsync(IdentityPublicKeyHash.FromSpki(fp));
        Assert.That(loaded, Is.Not.Null);
        Assert.That(loaded!.Id, Is.EqualTo(aggregate.Id));
    }

    [Test]
    public async Task Optimistic_concurrency_conflict_throws()
    {
        using var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        await using var db = CreateContext(conn);
        var repo = new SqlitePeerIdentityRepository(db);

        var now = new DateTimeOffset(2025, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var a = new PeerIdentity(new PeerId(3), new PublicIdentityId(Guid.NewGuid()));
        a.AddKey(Bytes(1), now, now.AddDays(1), now);
        await repo.SaveAsync(a);

        // simulate stale copy by directly creating a new aggregate with same id and default version
        var stale = new PeerIdentity(a.Id, new PublicIdentityId(Guid.NewGuid()));
        stale.AddKey(Bytes(2), now, now.AddDays(2), now);

        // First update should succeed
        a.SetDisplayName("v2");
        await repo.SaveAsync(a);

        // Stale should now conflict
        Assert.ThrowsAsync<InvalidOperationException>(async () => await repo.SaveAsync(stale));
    }
}
