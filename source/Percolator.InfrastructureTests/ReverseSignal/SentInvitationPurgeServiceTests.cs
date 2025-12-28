using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Percolator.Application.Identity;
using Percolator.Application.KeyExchange;
using Percolator.Application.ReverseSignal;
using Percolator.Cryptography;
using Percolator.Cryptography.Primitives;
using Percolator.Infrastructure.Cryptography;
using Percolator.Infrastructure.Persistence;
using Percolator.InfrastructureTests.Common;

namespace Percolator.InfrastructureTests.ReverseSignal;

[TestFixture]
public sealed class SentInvitationPurgeServiceTests
{
    private sealed class TestClock : IClock { public DateTimeOffset UtcNow { get; set; } }

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
    public async Task PurgeExpiredAsync_removes_expired_invite_and_burns_reserved_otk()
    {
        await using var ctx = CreateDbContext(out var conn);
        await using var _ = conn;

        var active = TestDb.CreateActiveIdentity(1);
        var activeAccessor = new MockActiveIdentityAccessor(active);

        var sentRepo = new SqliteSentInvitationRepository(ctx, active);
        var selfPreKeys = new SqliteSelfPreKeyBundleRepository(ctx);

        var clock = new TestClock { UtcNow = DateTimeOffset.UtcNow };

        var corr = new RequestCorrelationId(Guid.NewGuid());
        var expires = clock.UtcNow.AddSeconds(-1);
        var created = clock.UtcNow.AddMinutes(-5);

        await sentRepo.UpsertAsync(
            new SentInvitation(corr, Guid.NewGuid(), oneTimePreKeyId: Guid.NewGuid(), targetPeerId: null, created, expires),
            CancellationToken.None);

        // Seed an OTK and reserve it for the correlation id.
        var otkId = Guid.NewGuid();
        await selfPreKeys.SaveOneTimePreKeysAsync(1, new[] { (otkId, new byte[] { 0xAA }, new byte[] { 0xBB }) }, CancellationToken.None);
        var reserved = await selfPreKeys.TryReserveOneTimePreKeyAsync(1, corr.Value, expires, CancellationToken.None);
        reserved.Should().NotBeNull();

        var sut = new SentInvitationPurgeService(
            sentRepo,
            selfPreKeys,
            activeAccessor,
            active,
            clock,
            NullLogger<SentInvitationPurgeService>.Instance);

        var purged = await sut.PurgeExpiredAsync(CancellationToken.None);
        purged.Should().Be(1);

        var stillThere = await sentRepo.TryGetAsync(corr, CancellationToken.None);
        stillThere.Should().BeNull();

        // Reservation should be burned, so consumption should fail.
        var consumed = await selfPreKeys.TryConsumeReservedOneTimePreKeyPrivateAsync(1, corr.Value, clock.UtcNow, CancellationToken.None);
        consumed.Should().BeNull();
    }

    private sealed class MockActiveIdentityAccessor : IActiveIdentityAccessor
    {
        public bool IsActive { get; }

        public MockActiveIdentityAccessor(ActiveIdentityContext active)
        {
            IsActive = active.Identity is not null;
        }
    }
}
