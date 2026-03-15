using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Percolator.Cryptography;
using Percolator.Cryptography.Primitives;
using Percolator.Infrastructure.Cryptography;
using Percolator.Infrastructure.Persistence;
using Percolator.InfrastructureTests.Common;

namespace Percolator.InfrastructureTests.Cryptography;

[TestFixture]
public sealed class SqliteSentInvitationRepositoryTests
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

        if (!ctx.SelfIdentities.Any())
        {
            ctx.SelfIdentities.Add(new SelfIdentityDbo { Id = 1, PeerId = Guid.NewGuid(), Name = "default" });
            ctx.SaveChanges();
        }

        return ctx;
    }

    [Test]
    public async Task Upsert_and_TryGet_roundtrips_by_request_correlation_id()
    {
        await using var ctx = CreateDbContext(out var conn);
        await using var _ = conn;

        var repo = new SqliteSentInvitationRepository(ctx, TestDb.CreateActiveIdentity(1));

        var corr = new RequestCorrelationId(Guid.NewGuid());
        var spk = Guid.NewGuid();
        var otk = Guid.NewGuid();
        var created = DateTimeOffset.UtcNow;
        var expires = created.AddMinutes(10);

        var invite = new SentInvitation(corr, spk, otk, targetPeerId: null, created, expires);
        await repo.UpsertAsync(invite, CancellationToken.None);

        var loaded = await repo.TryGetAsync(corr, CancellationToken.None);
        loaded.Should().NotBeNull();
        loaded!.RequestCorrelationId.Should().Be(corr);
        loaded.SignedPreKeyId.Should().Be(spk);
        loaded.OneTimePreKeyId.Should().Be(otk);
        loaded.CreatedAtUtc.Should().Be(created);
        loaded.ExpiresAtUtc.Should().Be(expires);

        await repo.DeleteAsync(corr, CancellationToken.None);
        var afterDelete = await repo.TryGetAsync(corr, CancellationToken.None);
        afterDelete.Should().BeNull();
    }
}
