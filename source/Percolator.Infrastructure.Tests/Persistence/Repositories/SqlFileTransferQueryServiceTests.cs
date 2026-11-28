namespace Percolator.Infrastructure.Tests.Persistence.Repositories;

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FluentAssertions;
using NUnit.Framework;
using Percolator.Apps.FileTransfer.Domain.Common;
using Percolator.Apps.FileTransfer.Domain.ManifestManagement;
using Percolator.Apps.FileTransfer.Domain.SwarmCoordination;
using Percolator.Apps.FileTransfer.Domain.TransferNegotiation;
using Percolator.Apps.FileTransfer.Ports;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Identities;
using Percolator.Infrastructure.Persistence.Entities;
using Percolator.Infrastructure.Persistence.Repositories;
using Percolator.Infrastructure.Tests.Persistence.TestSupport;

[TestFixture]
public sealed class SqlFileTransferQueryServiceTests
{
    private SqliteTestDb _db = null!;
    private SqlFileTransferQueryService _queryService = null!;

    [SetUp]
    public void SetUp()
    {
        _db = new SqliteTestDb();
        _queryService = new SqlFileTransferQueryService(_db.ContextFactory);
    }

    [TearDown]
    public void TearDown()
    {
        _db.Dispose();
    }

    [Test]
    public async Task GetAllTransfersAsync_ProjectsVariousTopologiesAndUnlimitedSeedRatio()
    {
        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();
        var id3 = Guid.NewGuid();
        var channelId = Guid.NewGuid();

        var initiator1 = PublicIdentityId.New().ToByteArray();
        var recipient1 = PublicIdentityId.New().ToByteArray();
        var member1 = PublicIdentityId.New().ToByteArray();
        var member2 = PublicIdentityId.New().ToByteArray();

        await using (var db = _db.CreateNewContext())
        {
            // Session 1: OneToOne, Unlimited SeedRatio (-1.0)
            db.TransferSessions.Add(new TransferSessionEntity
            {
                TransferId = id1,
                InitiatorId = initiator1,
                TopologyType = 1,
                Role = (int)TransferRole.Sender,
                State = (int)TransferSessionState.Offered,
                RootName = "Session1",
                TotalSizeBytes = 100_000,
                ChunkSizeBytes = 10_000,
                TargetSeedRatio = -1.0,
                UploadedSizeBytes = 20_000,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                AllFilesSelected = true
            });
            db.TransferSessionRecipients.Add(new TransferSessionRecipientEntity
            {
                TransferId = id1,
                RecipientIdentityId = recipient1
            });
            db.TransferSessionTransportContexts.Add(new TransferSessionTransportContextEntity
            {
                TransferId = id1,
                ChannelId = channelId,
                LocalRootPath = @"C:\Transfers\Root1"
            });

            // Session 2: MultiPeer, Ratio 2.5
            db.TransferSessions.Add(new TransferSessionEntity
            {
                TransferId = id2,
                InitiatorId = initiator1,
                TopologyType = 2,
                Role = (int)TransferRole.Sender,
                State = (int)TransferSessionState.InFlight,
                RootName = "Session2",
                TotalSizeBytes = 50_000,
                ChunkSizeBytes = 10_000,
                TargetSeedRatio = 2.5,
                UploadedSizeBytes = 0,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                AllFilesSelected = true
            });
            db.TransferSessionRecipients.Add(new TransferSessionRecipientEntity
            {
                TransferId = id2,
                RecipientIdentityId = member1
            });
            db.TransferSessionRecipients.Add(new TransferSessionRecipientEntity
            {
                TransferId = id2,
                RecipientIdentityId = member2
            });

            // Session 3: IncomingStaged
            db.TransferSessions.Add(new TransferSessionEntity
            {
                TransferId = id3,
                InitiatorId = initiator1,
                TopologyType = 3,
                Role = (int)TransferRole.Recipient,
                State = (int)TransferSessionState.PendingConsent,
                RootName = "Session3",
                TotalSizeBytes = 20_000,
                ChunkSizeBytes = 10_000,
                TargetSeedRatio = 0.0,
                UploadedSizeBytes = 0,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                AllFilesSelected = false
            });

            await db.SaveChangesAsync();
        }

        IReadOnlyList<TransferSessionReadModel> transfers = await _queryService.GetAllTransfersAsync();

        transfers.Should().HaveCount(3);

        TransferSessionReadModel s1 = transfers.Should().ContainSingle(t => t.SessionId.Value == id1).Subject;
        s1.Topology.Should().BeOfType<TransferTopology.OneToOne>();
        s1.TargetSeedRatio.IsUnlimited.Should().BeTrue("TargetSeedRatio == -1.0 must project to SeedRatio.Unlimited without throwing");
        s1.LocalRootPath?.Value.Should().Be(@"C:\Transfers\Root1");
        s1.ChannelId?.Value.Should().Be(channelId);

        TransferSessionReadModel s2 = transfers.Should().ContainSingle(t => t.SessionId.Value == id2).Subject;
        s2.Topology.Should().BeOfType<TransferTopology.MultiPeer>();
        s2.TargetSeedRatio.Multiplier.Should().Be(2.5);

        TransferSessionReadModel s3 = transfers.Should().ContainSingle(t => t.SessionId.Value == id3).Subject;
        s3.Topology.Should().BeOfType<TransferTopology.IncomingStaged>();
    }

    [Test]
    public async Task GetTransferByIdAsync_CalculatesO1DownloadedBytesAccurately_WhenFinalPartialChunkVerified()
    {
        // Total size 98,304 bytes with 65,536-byte chunks -> 2 chunks:
        // Chunk 0: 65,536 bytes
        // Chunk 1: 32,768 bytes (partial)
        var transferGuid = Guid.NewGuid();
        var initiator = PublicIdentityId.New().ToByteArray();

        // Create bitfield with ONLY chunk 1 verified (chunk 0 is missing)
        var bitfield = Bitfield.Empty(ChunkCount.FromInt(2));
        bitfield = bitfield.WithChunk(ChunkIndex.FromInt(1));

        await using (var db = _db.CreateNewContext())
        {
            db.TransferSessions.Add(new TransferSessionEntity
            {
                TransferId = transferGuid,
                InitiatorId = initiator,
                TopologyType = 3,
                Role = (int)TransferRole.Recipient,
                State = (int)TransferSessionState.InFlight,
                RootName = "PartialChunkTest",
                TotalSizeBytes = 98_304,
                ChunkSizeBytes = 65_536,
                TargetSeedRatio = 1.0,
                UploadedSizeBytes = 16_384,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                AllFilesSelected = true
            });

            db.TransferBitfields.Add(new TransferBitfieldEntity
            {
                TransferId = transferGuid,
                BitfieldBytes = bitfield.ToByteArray(),
                TotalPieces = 2,
                VerifiedPieces = 1
            });

            await db.SaveChangesAsync();
        }

        TransferSessionReadModel? result = await _queryService.GetTransferByIdAsync(TransferSessionId.FromGuid(transferGuid));

        result.Should().NotBeNull();
        // Exact O(1) downloadedBytes calculation must account for the final chunk being 32,768 bytes, NOT 65,536!
        result!.DownloadedBytes.Bytes.Should().Be(32_768, "Only chunk 1 (32,768 bytes) is verified");
        result.CompletedChunks.Count.Should().Be(1);
        result.TotalChunks.Count.Should().Be(2);
        result.UploadedBytes.Bytes.Should().Be(16_384);
        result.CurrentSeedRatio.Multiplier.Should().Be(0.5, "16,384 / 32,768 == 0.5");
    }

    [Test]
    public async Task GetManifestFilesAsync_EvaluatesBitfieldCompletionAndSupportsPagination()
    {
        var transferGuid = Guid.NewGuid();
        var initiator = PublicIdentityId.New().ToByteArray();

        // 3 Files across 2 chunks (each chunk 65,536 bytes):
        // File 0: 32,768 bytes (offset 0, chunk 0) -> Chunk 0
        // File 1: 0 bytes (offset 32,768, chunk 0) -> Empty file
        // File 2: 40,000 bytes (offset 32,768, spans chunk 0 and chunk 1: 32,768..72,768) -> Chunks 0 and 1
        // File 3: 100 bytes (offset 72,768, chunk 1) -> Chunk 1
        var bitfield = Bitfield.Empty(ChunkCount.FromInt(2));
        bitfield = bitfield.WithChunk(ChunkIndex.FromInt(0)); // Only chunk 0 is verified, chunk 1 is missing

        await using (var db = _db.CreateNewContext())
        {
            db.TransferSessions.Add(new TransferSessionEntity
            {
                TransferId = transferGuid,
                InitiatorId = initiator,
                TopologyType = 3,
                Role = (int)TransferRole.Recipient,
                State = (int)TransferSessionState.InFlight,
                RootName = "ManifestTest",
                TotalSizeBytes = 72_868,
                ChunkSizeBytes = 65_536,
                TargetSeedRatio = 0.0,
                UploadedSizeBytes = 0,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                AllFilesSelected = false // Partial selection: files 0, 1, 2 selected; file 3 unselected
            });

            db.TransferBitfields.Add(new TransferBitfieldEntity
            {
                TransferId = transferGuid,
                BitfieldBytes = bitfield.ToByteArray(),
                TotalPieces = 2,
                VerifiedPieces = 1
            });

            db.ManifestFiles.AddRange(
                new ManifestFileEntity { TransferId = transferGuid, FileIndex = 0, RelativePath = "f0.dat", FileSizeBytes = 32_768, StartOffset = 0, LastModifiedUnixSeconds = 1700000000 },
                new ManifestFileEntity { TransferId = transferGuid, FileIndex = 1, RelativePath = "f1_empty.dat", FileSizeBytes = 0, StartOffset = 32_768, LastModifiedUnixSeconds = 1700000000 },
                new ManifestFileEntity { TransferId = transferGuid, FileIndex = 2, RelativePath = "f2_spanning.dat", FileSizeBytes = 40_000, StartOffset = 32_768, LastModifiedUnixSeconds = 1700000000 },
                new ManifestFileEntity { TransferId = transferGuid, FileIndex = 3, RelativePath = "f3_unselected.dat", FileSizeBytes = 100, StartOffset = 72_768, LastModifiedUnixSeconds = 1700000000 }
            );

            db.TransferSessionSelectedFiles.AddRange(
                new TransferSessionSelectedFileEntity { TransferId = transferGuid, FileIndex = 0 },
                new TransferSessionSelectedFileEntity { TransferId = transferGuid, FileIndex = 1 },
                new TransferSessionSelectedFileEntity { TransferId = transferGuid, FileIndex = 2 }
            );

            await db.SaveChangesAsync();
        }

        // Full fetch
        IReadOnlyList<ManifestFileReadModel> allFiles = await _queryService.GetManifestFilesAsync(TransferSessionId.FromGuid(transferGuid));

        allFiles.Should().HaveCount(4);

        // File 0: chunk 0 is verified -> IsCompleted == true, IsSelected == true
        allFiles[0].IsSelected.Should().BeTrue();
        allFiles[0].IsCompleted.Should().BeTrue();

        // File 1: 0-byte file -> IsCompleted == true
        allFiles[1].IsSelected.Should().BeTrue();
        allFiles[1].IsCompleted.Should().BeTrue();

        // File 2: spans chunks 0 and 1; chunk 1 is not verified -> IsCompleted == false
        allFiles[2].IsSelected.Should().BeTrue();
        allFiles[2].IsCompleted.Should().BeFalse();

        // File 3: unselected -> IsSelected == false, IsCompleted == false
        allFiles[3].IsSelected.Should().BeFalse();
        allFiles[3].IsCompleted.Should().BeFalse();

        // Pagination test: skip 1, take 2
        IReadOnlyList<ManifestFileReadModel> paginated = await _queryService.GetManifestFilesAsync(TransferSessionId.FromGuid(transferGuid), skip: 1, take: 2);
        paginated.Should().HaveCount(2);
        paginated[0].Index.Value.Should().Be(1);
        paginated[1].Index.Value.Should().Be(2);
    }

    [Test]
    public async Task ConcurrentQueries_ExecuteSafelyOnSingletonInstance()
    {
        var id = Guid.NewGuid();
        await using (var db = _db.CreateNewContext())
        {
            db.TransferSessions.Add(new TransferSessionEntity
            {
                TransferId = id,
                InitiatorId = PublicIdentityId.New().ToByteArray(),
                TopologyType = 3,
                Role = (int)TransferRole.Sender,
                State = (int)TransferSessionState.Offered,
                RootName = "ConcurrentSession",
                TotalSizeBytes = 1024,
                ChunkSizeBytes = 512,
                TargetSeedRatio = 1.0,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                AllFilesSelected = true
            });
            await db.SaveChangesAsync();
        }

        var t1 = _queryService.GetAllTransfersAsync().AsTask();
        var t2 = _queryService.GetTransferByIdAsync(TransferSessionId.FromGuid(id)).AsTask();
        var t3 = _queryService.GetManifestFilesAsync(TransferSessionId.FromGuid(id)).AsTask();

        var act = async () => await Task.WhenAll(t1, t2, t3);
        await act.Should().NotThrowAsync();
    }
}
