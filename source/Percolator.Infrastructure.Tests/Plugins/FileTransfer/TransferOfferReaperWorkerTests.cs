namespace Percolator.Infrastructure.Tests.Plugins.FileTransfer;

using System;
using System.Security.Cryptography;
using System.Threading.Tasks;
using FluentAssertions;
using NUnit.Framework;
using Percolator.Apps.FileTransfer.Domain.Common;
using Percolator.Apps.FileTransfer.Domain.ManifestManagement;
using Percolator.Apps.FileTransfer.Domain.TransferNegotiation;
using Percolator.Apps.FileTransfer.Features.TransferNegotiation;
using Percolator.Domain.Common;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Infrastructure.Persistence.Repositories;
using Percolator.Infrastructure.Tests.Persistence.TestSupport;

[TestFixture]
public sealed class TransferOfferReaperWorkerTests
{
    private SqliteTestDb _db = null!;
    private SqliteTransferSessionRepository _sessionRepo = null!;

    [SetUp]
    public void SetUp()
    {
        _db = new SqliteTestDb();
        _sessionRepo = new SqliteTransferSessionRepository(_db.DbContext);
    }

    [TearDown]
    public void TearDown()
    {
        _db.Dispose();
    }

    [Test]
    public async Task StaleOffers_OlderThan24h_Reaped()
    {
        var sessionId = TransferSessionId.New();
        var initiatorId = PublicIdentityId.New();

        byte[] payload = new byte[1024];
        RandomNumberGenerator.Fill(payload);

        var leaf = MerkleTreeBuilder.HashLeaf(payload);
        var merkleTree = MerkleTreeIndex.Create(MerkleRoot.FromSpan(leaf.Span), [leaf]);
        var fileEntry = new FileEntry(
            FileIndex.FromInt(0),
            SanitizedRelativePath.Parse("doc.txt"),
            FileSizeBytes.FromBytes(1024),
            BundleByteOffset.Zero,
            FileLastModifiedUtc.FromDateTimeOffset(DateTimeOffset.UtcNow));

        var manifest = TransferManifest.Create(
            sessionId,
            RootBundleName.Parse("PendingBundle"),
            ChunkSizeBytes.FromBytes(65536),
            merkleTree,
            [fileEntry]);

        DateTimeOffset creationTime = DateTimeOffset.UtcNow.AddHours(-25);
        var creationClock = new TestClock(creationTime);

        // Create incoming staged session (State = PendingConsent)
        var session = TransferSession.CreateIncomingStaged(
            sessionId,
            initiatorId,
            manifest,
            SeedRatio.Zero,
            creationClock);

        await _sessionRepo.SaveSessionAsync(session);

        // Advance virtual clock to now (25 hours later)
        var currentClock = new TestClock(DateTimeOffset.UtcNow);
        var reaperService = new TransferOfferReaperService(_sessionRepo, currentClock);

        int reaped = await reaperService.SweepAndExpireStaleOffersAsync(TimeSpan.FromHours(24));
        reaped.Should().Be(1);

        // Verify session state updated in database
        using var readDb = _db.CreateNewContext();
        var readRepo = new SqliteTransferSessionRepository(readDb);

        var loaded = await readRepo.GetSessionByIdAsync(sessionId);
        loaded.Should().NotBeNull();
        loaded!.State.Should().Be(TransferSessionState.Cancelled);
    }

    private sealed class TestClock : IDateTimeProvider
    {
        public DateTimeOffset UtcNow { get; }
        public TestClock(DateTimeOffset now) => UtcNow = now;
    }
}
