namespace Percolator.Infrastructure.Tests.Transport.Primitives;

using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;
using Percolator.Application.Ports;
using Percolator.Domain.Common;
using Percolator.Infrastructure.Persistence;
using Percolator.Infrastructure.Persistence.Entities;
using Percolator.Infrastructure.Tests.Persistence.TestSupport;
using Percolator.Infrastructure.Transport;

[TestFixture]
public sealed class SqlitePortReservationServiceTests
{
    private SqliteTestDb _testDb = null!;
    private TestContextFactory _contextFactory = null!;
    private MutableTimeProvider _timeProvider = null!;

    private sealed class MutableTimeProvider : IDateTimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.UtcNow;
    }

    private sealed class TestContextFactory : IDbContextFactory<PercolatorDbContext>
    {
        private readonly SqliteTestDb _db;

        public TestContextFactory(SqliteTestDb db)
        {
            _db = db;
        }

        public PercolatorDbContext CreateDbContext() => _db.CreateNewContext();
        public Task<PercolatorDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(_db.CreateNewContext());
    }

    [SetUp]
    public void SetUp()
    {
        _testDb = new SqliteTestDb();
        _contextFactory = new TestContextFactory(_testDb);
        _timeProvider = new MutableTimeProvider();
    }

    [TearDown]
    public void TearDown()
    {
        _testDb.Dispose();
    }

    [Test]
    public async Task PortContention_BindsNextAvailablePort()
    {
        // Simulate port 9000 being unavailable/in-use via socket prober, but 9001 being available
        bool CustomSocketProber(int port)
        {
            if (port == 9000) return false; // Port collision / contended
            return true;
        }

        var service = new SqlitePortReservationService(_contextFactory, _timeProvider, CustomSocketProber);
        var range = new PortRange(new PortNumber(9000), new PortNumber(9005));

        var result = await service.AcquirePortLeaseAsync(range);

        result.IsSuccess.Should().BeTrue();
        result.Value.Port.Value.Should().Be(9001);
        result.Value.LeaseToken.Value.Should().NotBeEmpty();
        result.Value.ExpiresAtUtc.Should().Be(_timeProvider.UtcNow.AddMinutes(30));

        // Attempting to acquire again in the same range: 9000 is still busy, 9001 is active in DB, so 9002 is bound
        var result2 = await service.AcquirePortLeaseAsync(range);
        result2.IsSuccess.Should().BeTrue();
        result2.Value.Port.Value.Should().Be(9002);
    }

    [Test]
    public async Task PurgeExpiredLeases_RemovesStaleEntries_PreservesActiveLeases()
    {
        var service = new SqlitePortReservationService(_contextFactory, _timeProvider, _ => true);

        var now = _timeProvider.UtcNow;

        // Seed 1 active lease and 1 expired lease directly in the DB
        await using (var db = _contextFactory.CreateDbContext())
        {
            db.OpsReservedPorts.Add(new OpsReservedPortEntity
            {
                PortNumber = 8001,
                LeaseToken = Guid.NewGuid(),
                AllocatedAtUtc = now.AddHours(-2),
                LeaseExpiryUtc = now.AddHours(-1) // Stale / expired
            });

            db.OpsReservedPorts.Add(new OpsReservedPortEntity
            {
                PortNumber = 8002,
                LeaseToken = Guid.NewGuid(),
                AllocatedAtUtc = now,
                LeaseExpiryUtc = now.AddMinutes(30) // Active
            });

            await db.SaveChangesAsync();
        }

        // Purge expired leases
        var purgeResult = await service.PurgeExpiredLeasesAsync();
        purgeResult.IsSuccess.Should().BeTrue();

        // Verify active lease remains, expired lease removed
        await using (var db = _contextFactory.CreateDbContext())
        {
            var port8001 = await db.OpsReservedPorts.FindAsync(8001);
            var port8002 = await db.OpsReservedPorts.FindAsync(8002);

            port8001.Should().BeNull();
            port8002.Should().NotBeNull();
            port8002!.PortNumber.Should().Be(8002);
        }
    }
}
