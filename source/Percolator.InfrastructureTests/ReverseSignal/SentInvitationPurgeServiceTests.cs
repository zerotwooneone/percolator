using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Percolator.Application.Identity;
using Percolator.Application.ReverseSignal;
using Percolator.Cryptography;
using Percolator.Cryptography.Primitives;
using Percolator.Identity;
using Percolator.Infrastructure.Cryptography;
using Percolator.Infrastructure.Persistence;
using Percolator.InfrastructureTests.Common;

namespace Percolator.InfrastructureTests.ReverseSignal;

[TestFixture]
public sealed class SentInvitationPurgeServiceTests
{
    private sealed class TestClock : IClock
    {
        private static readonly DateTimeOffset FixedTime = new DateTimeOffset(2025, 1, 1, 12, 0, 0, TimeSpan.Zero);
        public DateTimeOffset UtcNow { get; set; } = FixedTime;
    }

    private static PercolatorDbContext CreateDbContext(out SqliteConnection connection)
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<PercolatorDbContext>()
            .UseSqlite(connection)
            .Options;

        // Create schema without ActiveIdentityContext
        using var schemaCtx = new PercolatorDbContext(options);
        schemaCtx.Database.EnsureCreated();

        // Return context without ActiveIdentityContext
        return new PercolatorDbContext(options);
    }

    [Test]
    public async Task PurgeExpiredAsync_removes_expired_invite_and_burns_reserved_otk()
    {
        await using var ctx = CreateDbContext(out var conn);
        await using var _ = conn;

        // Seed SelfIdentity
        ctx.SelfIdentities.Add(new SelfIdentityDbo 
        { 
            Id = 1, 
            PublicIdentityId = new PublicIdentityId(Guid.NewGuid()), 
            Name = "test",
            DeviceId = new Percolator.Identity.DeviceId(1),
            ListeningPort = new Percolator.Identity.Model.ListeningPort(5000),
            LastUsedUtc = DateTimeOffset.UtcNow
        });
        ctx.SaveChanges();

        var active = TestDb.CreateActiveIdentity(1);
        var activeAccessor = new MockActiveIdentityAccessor(active);

        var sentRepo = new SqliteSentInvitationRepository(ctx);
        var selfPreKeys = new SqliteSelfPreKeyBundleRepository(ctx);

        var clock = new TestClock { UtcNow = new DateTimeOffset(2025, 1, 1, 12, 0, 0, TimeSpan.Zero) };

        var corr = new RequestCorrelationId(Guid.NewGuid());
        var expires = clock.UtcNow.AddSeconds(-1);
        var created = clock.UtcNow.AddMinutes(-5);

        await sentRepo.UpsertAsync(
            new SentInvitation(corr, Guid.NewGuid(), oneTimePreKeyId: Guid.NewGuid(), targetPeerId: null, created, expires),
            new CryptoSelfId(1),
            CancellationToken.None);

        // Seed an OTK and reserve it for the correlation id.
        var otkId = Guid.NewGuid();
        await selfPreKeys.SaveOneTimePreKeysAsync(new SelfId(1), new[] { (otkId, new byte[] { 0xAA }, new byte[] { 0xBB }) }, CancellationToken.None);
        var reserved = await selfPreKeys.TryReserveOneTimePreKeyAsync(new SelfId(1), corr.Value, expires, CancellationToken.None);
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

        var stillThere = await sentRepo.TryGetAsync(corr, new CryptoSelfId(1), CancellationToken.None);
        stillThere.Should().BeNull();

        // Reservation should be burned, so consumption should fail.
        var consumed = await selfPreKeys.TryConsumeReservedOneTimePreKeyPrivateAsync(new SelfId(1), corr.Value, clock.UtcNow, CancellationToken.None);
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
