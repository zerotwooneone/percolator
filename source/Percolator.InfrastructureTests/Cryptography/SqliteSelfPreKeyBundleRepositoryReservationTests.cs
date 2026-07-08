using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Percolator.Identity;
using Percolator.Infrastructure.Cryptography;
using Percolator.Infrastructure.Persistence;
using Percolator.InfrastructureTests.Common;

namespace Percolator.InfrastructureTests.Cryptography;

[TestFixture]
public sealed class SqliteSelfPreKeyBundleRepositoryReservationTests
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
            ctx.SelfIdentities.Add(new SelfIdentityDbo { Id = 1, PublicIdentityId = Guid.NewGuid(), Name = "default", DeviceId = new DeviceId(1), ListeningPort = new Percolator.Identity.Model.ListeningPort(5000), LastUsedUtc = DateTimeOffset.UtcNow });
            ctx.SaveChanges();
        }

        return ctx;
    }

    [Test]
    public async Task TryReserveOneTimePreKeyAsync_is_exclusive_per_requestCorrelationId()
    {
        await using var ctx = CreateDbContext(out var conn);
        await using var _ = conn;

        var repo = new SqliteSelfPreKeyBundleRepository(ctx);

        var otkId = Guid.NewGuid();
        await repo.SaveOneTimePreKeysAsync(new SelfId(1), new[]
        {
            (otkId, new byte[] { 0xAA }, new byte[] { 0xBB })
        }, CancellationToken.None);

        var correlationId = Guid.NewGuid();
        var fixedTime = new DateTimeOffset(2025, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var until = fixedTime.AddMinutes(5);

        var first = await repo.TryReserveOneTimePreKeyAsync(new SelfId(1), correlationId, until, CancellationToken.None);
        first.Should().NotBeNull();

        var second = await repo.TryReserveOneTimePreKeyAsync(new SelfId(1), correlationId, until, CancellationToken.None);
        second.Should().BeNull();
    }

    [Test]
    public async Task TryConsumeReservedOneTimePreKeyPrivateAsync_consumes_exactly_once()
    {
        await using var ctx = CreateDbContext(out var conn);
        await using var _ = conn;

        var repo = new SqliteSelfPreKeyBundleRepository(ctx);

        var otkId = Guid.NewGuid();
        var priv = new byte[] { 0x01, 0x02, 0x03 };
        await repo.SaveOneTimePreKeysAsync(new SelfId(1), new[]
        {
            (otkId, priv, new byte[] { 0xBB })
        }, CancellationToken.None);

        var correlationId = Guid.NewGuid();
        var fixedTime = new DateTimeOffset(2025, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var until = fixedTime.AddMinutes(5);
        var reserved = await repo.TryReserveOneTimePreKeyAsync(new SelfId(1), correlationId, until, CancellationToken.None);
        reserved.Should().NotBeNull();
        reserved!.Value.otkId.Should().Be(otkId);

        var consumed1 = await repo.TryConsumeReservedOneTimePreKeyPrivateAsync(new SelfId(1), correlationId, fixedTime, CancellationToken.None);
        consumed1.Should().NotBeNull();
        consumed1!.Should().Equal(priv);

        var consumed2 = await repo.TryConsumeReservedOneTimePreKeyPrivateAsync(new SelfId(1), correlationId, fixedTime, CancellationToken.None);
        consumed2.Should().BeNull();
    }

    [Test]
    public async Task PurgeExpiredReservedOneTimePreKeysAsync_removes_expired_and_blocks_consumption()
    {
        await using var ctx = CreateDbContext(out var conn);
        await using var _ = conn;

        var repo = new SqliteSelfPreKeyBundleRepository(ctx);

        var otkId = Guid.NewGuid();
        await repo.SaveOneTimePreKeysAsync(new SelfId(1), new[]
        {
            (otkId, new byte[] { 0x11 }, new byte[] { 0x22 })
        }, CancellationToken.None);

        var correlationId = Guid.NewGuid();
        var fixedTime = new DateTimeOffset(2025, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var until = fixedTime.AddSeconds(-1);
        var reserved = await repo.TryReserveOneTimePreKeyAsync(new SelfId(1), correlationId, until, CancellationToken.None);
        reserved.Should().NotBeNull();

        var purged = await repo.PurgeExpiredReservedOneTimePreKeysAsync(new SelfId(1), fixedTime, CancellationToken.None);
        purged.Should().Be(1);

        var consumed = await repo.TryConsumeReservedOneTimePreKeyPrivateAsync(new SelfId(1), correlationId, fixedTime, CancellationToken.None);
        consumed.Should().BeNull();
    }

    [Test]
    public async Task TryBurnReservedOneTimePreKeyAsync_removes_reservation_and_blocks_consumption()
    {
        await using var ctx = CreateDbContext(out var conn);
        await using var _ = conn;

        var repo = new SqliteSelfPreKeyBundleRepository(ctx);

        var otkId = Guid.NewGuid();
        await repo.SaveOneTimePreKeysAsync(new SelfId(1), new[]
        {
            (otkId, new byte[] { 0x11 }, new byte[] { 0x22 })
        }, CancellationToken.None);

        var correlationId = Guid.NewGuid();
        var fixedTime = new DateTimeOffset(2025, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var until = fixedTime.AddMinutes(5);
        var reserved = await repo.TryReserveOneTimePreKeyAsync(new SelfId(1), correlationId, until, CancellationToken.None);
        reserved.Should().NotBeNull();

        var burned = await repo.TryBurnReservedOneTimePreKeyAsync(new SelfId(1), correlationId, fixedTime, CancellationToken.None);
        burned.Should().BeTrue();

        var consumed = await repo.TryConsumeReservedOneTimePreKeyPrivateAsync(new SelfId(1), correlationId, fixedTime, CancellationToken.None);
        consumed.Should().BeNull();
    }
}
