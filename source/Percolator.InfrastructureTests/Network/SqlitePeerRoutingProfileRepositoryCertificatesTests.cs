using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Percolator.Infrastructure.Network;
using Percolator.Infrastructure.Persistence;
using Percolator.Network;

namespace Percolator.InfrastructureTests.Network;

[TestFixture]
public class SqlitePeerRoutingProfileRepositoryCertificatesTests
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
    public async Task RotateCertificates_persists_and_hydrates_current_set()
    {
        var ctx = CreateDbContext(out var _);
        var repo = new SqlitePeerRoutingProfileRepository(ctx);
        var pid = PeerId.NewId();
        var prp = new PeerRoutingProfile();
        prp.BindIdentity(pid);

        // First rotation
        var t0 = DateTimeOffset.UtcNow.AddMinutes(-5);
        var c0a = new TlsCertificate(new byte[] { 1, 2, 3 });
        var c0b = new TlsCertificate(new byte[] { 4, 5, 6 });
        prp.RotateCertificates(new[] { c0a, c0b }, t0);
        await repo.UpsertAsync(prp);

        // Second rotation replaces set
        var t1 = DateTimeOffset.UtcNow;
        var c1a = new TlsCertificate(new byte[] { 7, 8, 9 });
        prp.RotateCertificates(new[] { c1a }, t1);
        await repo.UpsertAsync(prp);

        var loaded = await repo.GetByIdAsync(pid);
        loaded.Should().NotBeNull();
        loaded!.Certificates.Should().HaveCount(1);
        loaded.Certificates.Single().RawData.Should().BeEquivalentTo(c1a.RawData);
    }
}
