namespace Percolator.Infrastructure.Tests.Plugins.Discovery;

using System;
using System.Security.Cryptography;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Infrastructure.Persistence.Entities;
using Percolator.Infrastructure.Tests.Persistence.TestSupport;
using Percolator.Infrastructure.Transport.Workers;

[TestFixture]
public sealed class RelayDiscoveryRegistrationPrunerTests
{
    private sealed class TestTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow;
        public TestTimeProvider(DateTimeOffset initial) => _utcNow = initial;
        public override DateTimeOffset GetUtcNow() => _utcNow;
        public void Advance(TimeSpan delta) => _utcNow = _utcNow.Add(delta);
    }

    [Test]
    public async Task StaleRegistrations_PurgedOnSchedule()
    {
        using var testDb = new SqliteTestDb();
        var startTime = new DateTimeOffset(2026, 1, 10, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new TestTimeProvider(startTime);

        var pruner = new RelayDiscoveryRegistrationPruner(testDb.DbContext, timeProvider);

        var stalePeerId = PublicIdentityId.New();
        var freshPeerId = PublicIdentityId.New();

        // 1. Insert directory entries: stale (>24h) and fresh (<24h)
        testDb.DbContext.RelayPeerDirectory.Add(new RelayPeerDirectoryEntity
        {
            PeerId = stalePeerId.Value.ToByteArray(),
            Visibility = 1,
            HasAuthenticatedHandshake = true,
            HasActiveSignedPreKey = true,
            RegisteredAtUtc = startTime.AddHours(-30),
            LastUpdatedAtUtc = startTime.AddHours(-25)
        });

        testDb.DbContext.RelayPeerDirectory.Add(new RelayPeerDirectoryEntity
        {
            PeerId = freshPeerId.Value.ToByteArray(),
            Visibility = 1,
            HasAuthenticatedHandshake = true,
            HasActiveSignedPreKey = true,
            RegisteredAtUtc = startTime.AddHours(-2),
            LastUpdatedAtUtc = startTime.AddHours(-2)
        });

        // 2. Insert rendezvous entries: stale (>48h) and fresh (<48h)
        byte[] staleLoc = new byte[32];
        byte[] freshLoc = new byte[32];
        RandomNumberGenerator.Fill(staleLoc);
        RandomNumberGenerator.Fill(freshLoc);

        testDb.DbContext.RelayRendezvousEntries.Add(new RelayRendezvousEntryEntity
        {
            BlindedLocator = staleLoc,
            EncryptedRoute = new byte[64],
            PublishedAtUtc = startTime.AddHours(-50)
        });

        testDb.DbContext.RelayRendezvousEntries.Add(new RelayRendezvousEntryEntity
        {
            BlindedLocator = freshLoc,
            EncryptedRoute = new byte[64],
            PublishedAtUtc = startTime.AddHours(-5)
        });

        await testDb.DbContext.SaveChangesAsync();

        // Act: Run sweep
        int purged = await pruner.SweepAsync();

        // Assert: Exactly 2 stale entries purged
        purged.Should().Be(2);

        // Verify database contents
        var remainingDirectory = await testDb.DbContext.RelayPeerDirectory.ToListAsync();
        remainingDirectory.Should().ContainSingle();
        remainingDirectory[0].PeerId.Should().Equal(freshPeerId.Value.ToByteArray());

        var remainingRendezvous = await testDb.DbContext.RelayRendezvousEntries.ToListAsync();
        remainingRendezvous.Should().ContainSingle();
        remainingRendezvous[0].BlindedLocator.Should().Equal(freshLoc);
    }
}
