using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;
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
    public async Task TryReserveOneTimePreKeyAsync_is_exclusive_per_requestCorrelationId()
    {
        await using var ctx = CreateDbContext(out var conn);
        await using var _ = conn;

        var repo = new SqliteSelfPreKeyBundleRepository(ctx);

        var otkId = Guid.NewGuid();
        await repo.SaveOneTimePreKeysAsync(1, new[]
        {
            (otkId, new byte[] { 0xAA }, new byte[] { 0xBB })
        }, CancellationToken.None);

        var correlationId = Guid.NewGuid();
        var until = DateTimeOffset.UtcNow.AddMinutes(5);

        var first = await repo.TryReserveOneTimePreKeyAsync(1, correlationId, until, CancellationToken.None);
        first.Should().NotBeNull();

        var second = await repo.TryReserveOneTimePreKeyAsync(1, correlationId, until, CancellationToken.None);
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
        await repo.SaveOneTimePreKeysAsync(1, new[]
        {
            (otkId, priv, new byte[] { 0xBB })
        }, CancellationToken.None);

        var correlationId = Guid.NewGuid();
        var until = DateTimeOffset.UtcNow.AddMinutes(5);
        var reserved = await repo.TryReserveOneTimePreKeyAsync(1, correlationId, until, CancellationToken.None);
        reserved.Should().NotBeNull();
        reserved!.Value.otkId.Should().Be(otkId);

        var consumed1 = await repo.TryConsumeReservedOneTimePreKeyPrivateAsync(1, correlationId, DateTimeOffset.UtcNow, CancellationToken.None);
        consumed1.Should().NotBeNull();
        consumed1!.Should().Equal(priv);

        var consumed2 = await repo.TryConsumeReservedOneTimePreKeyPrivateAsync(1, correlationId, DateTimeOffset.UtcNow, CancellationToken.None);
        consumed2.Should().BeNull();
    }

    [Test]
    public async Task PurgeExpiredReservedOneTimePreKeysAsync_removes_expired_and_blocks_consumption()
    {
        await using var ctx = CreateDbContext(out var conn);
        await using var _ = conn;

        var repo = new SqliteSelfPreKeyBundleRepository(ctx);

        var otkId = Guid.NewGuid();
        await repo.SaveOneTimePreKeysAsync(1, new[]
        {
            (otkId, new byte[] { 0x11 }, new byte[] { 0x22 })
        }, CancellationToken.None);

        var correlationId = Guid.NewGuid();
        var until = DateTimeOffset.UtcNow.AddSeconds(-1);
        var reserved = await repo.TryReserveOneTimePreKeyAsync(1, correlationId, until, CancellationToken.None);
        reserved.Should().NotBeNull();

        var purged = await repo.PurgeExpiredReservedOneTimePreKeysAsync(1, DateTimeOffset.UtcNow, CancellationToken.None);
        purged.Should().Be(1);

        var consumed = await repo.TryConsumeReservedOneTimePreKeyPrivateAsync(1, correlationId, DateTimeOffset.UtcNow, CancellationToken.None);
        consumed.Should().BeNull();
    }

    [Test]
    public async Task TryBurnReservedOneTimePreKeyAsync_removes_reservation_and_blocks_consumption()
    {
        await using var ctx = CreateDbContext(out var conn);
        await using var _ = conn;

        var repo = new SqliteSelfPreKeyBundleRepository(ctx);

        var otkId = Guid.NewGuid();
        await repo.SaveOneTimePreKeysAsync(1, new[]
        {
            (otkId, new byte[] { 0x11 }, new byte[] { 0x22 })
        }, CancellationToken.None);

        var correlationId = Guid.NewGuid();
        var until = DateTimeOffset.UtcNow.AddMinutes(5);
        var reserved = await repo.TryReserveOneTimePreKeyAsync(1, correlationId, until, CancellationToken.None);
        reserved.Should().NotBeNull();

        var burned = await repo.TryBurnReservedOneTimePreKeyAsync(1, correlationId, DateTimeOffset.UtcNow, CancellationToken.None);
        burned.Should().BeTrue();

        var consumed = await repo.TryConsumeReservedOneTimePreKeyPrivateAsync(1, correlationId, DateTimeOffset.UtcNow, CancellationToken.None);
        consumed.Should().BeNull();
    }
}
