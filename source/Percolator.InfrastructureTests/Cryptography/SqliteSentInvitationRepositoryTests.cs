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

        var ctx = TestDb.NewContextWithSchema(options, 1);

        if (!ctx.SelfIdentities.Any())
        {
            ctx.SelfIdentities.Add(new SelfIdentityDbo { Id = 1, PublicIdentityId = Guid.NewGuid(), Name = "default", DeviceId = 1, ListeningPort = 5000, LastUsedUtc = DateTimeOffset.UtcNow });
            ctx.SaveChanges();
        }

        return ctx;
    }

    [Test]
    public async Task Upsert_and_TryGet_roundtrips_by_request_correlation_id()
    {
        await using var ctx = CreateDbContext(out var conn);
        await using var _ = conn;

        var repo = new SqliteSentInvitationRepository(ctx);

        var corr = new RequestCorrelationId(Guid.NewGuid());
        var spk = Guid.NewGuid();
        var otk = Guid.NewGuid();
        var fixedTime = new DateTimeOffset(2025, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var created = fixedTime;
        var expires = created.AddMinutes(10);

        var invite = new SentInvitation(
            corr,
            spk,
            otk,
            targetPeerId: null,
            created,
            expires,
            targetDisplayName: "Alice",
            targetEndpointHost: "127.0.0.1",
            targetEndpointPort: 5002);
        await repo.UpsertAsync(invite, new CryptoSelfId(1), CancellationToken.None);

        var loaded = await repo.TryGetAsync(corr, new CryptoSelfId(1), CancellationToken.None);
        loaded.Should().NotBeNull();
        loaded!.RequestCorrelationId.Should().Be(corr);
        loaded.SignedPreKeyId.Should().Be(spk);
        loaded.OneTimePreKeyId.Should().Be(otk);
        loaded.CreatedAtUtc.Should().Be(created);
        loaded.ExpiresAtUtc.Should().Be(expires);
        loaded.TargetDisplayName.Should().Be("Alice");
        loaded.TargetEndpointHost.Should().Be("127.0.0.1");
        loaded.TargetEndpointPort.Should().Be(5002);

        await repo.DeleteAsync(corr, new CryptoSelfId(1), CancellationToken.None);
        var afterDelete = await repo.TryGetAsync(corr, new CryptoSelfId(1), CancellationToken.None);
        afterDelete.Should().BeNull();
    }
}
