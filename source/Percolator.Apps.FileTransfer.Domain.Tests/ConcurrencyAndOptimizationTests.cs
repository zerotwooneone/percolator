using FluentAssertions;
using NUnit.Framework;
using Percolator.Apps.FileTransfer.Domain.Common;
using Percolator.Apps.FileTransfer.Domain.Exceptions;
using Percolator.Apps.FileTransfer.Domain.ManifestManagement;
using Percolator.Apps.FileTransfer.Domain.Tests.TestDoubles;
using Percolator.Apps.FileTransfer.Domain.TransferNegotiation;
using Percolator.Apps.FileTransfer.Domain.TransferNegotiation.Events;
using PublicIdentityId = Percolator.Domain.Identities.PublicIdentityId;

namespace Percolator.Apps.FileTransfer.Domain.Tests;

[TestFixture]
public sealed class ConcurrencyAndOptimizationTests
{
    private DateTimeOffset _testTime;
    private FakeDateTimeProvider _clock = null!;

    [SetUp]
    public void SetUp()
    {
        _testTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        _clock = new FakeDateTimeProvider();
    }

    [Test]
    public void DT_OPT_01_BitfieldBuilder_SetChunkAndSetRange_BuildsAccurately()
    {
        ChunkCount total = ChunkCount.FromInt(25);
        Bitfield.BitfieldBuilder builder = new(total);

        builder.Set(ChunkIndex.FromInt(0));
        builder.SetRange(8, 11); // sets indices 8, 9, 10, 11 (4 bits in byte 1)
        builder.Set(ChunkIndex.FromInt(24)); // last bit in byte 3

        Bitfield bf = builder.Build();

        bf.TotalChunks.Count.Should().Be(25);
        bf.PopCount.Should().Be(6);

        bf.HasChunk(ChunkIndex.FromInt(0)).Should().BeTrue();
        bf.HasChunk(ChunkIndex.FromInt(1)).Should().BeFalse();
        bf.HasChunk(ChunkIndex.FromInt(7)).Should().BeFalse();
        bf.HasChunk(ChunkIndex.FromInt(8)).Should().BeTrue();
        bf.HasChunk(ChunkIndex.FromInt(9)).Should().BeTrue();
        bf.HasChunk(ChunkIndex.FromInt(10)).Should().BeTrue();
        bf.HasChunk(ChunkIndex.FromInt(11)).Should().BeTrue();
        bf.HasChunk(ChunkIndex.FromInt(12)).Should().BeFalse();
        bf.HasChunk(ChunkIndex.FromInt(24)).Should().BeTrue();

        // Trailing bits beyond 24 must be zero
        Action outOfBoundsCheck = () => builder.Set(ChunkIndex.FromInt(25));
        outOfBoundsCheck.Should().Throw<DomainValidationException>();

        Action invalidRangeCheck = () => builder.SetRange(10, 5);
        invalidRangeCheck.Should().Throw<DomainValidationException>();
    }

    [Test]
    public void DT_OPT_02_Bitfield_Union_And_HasAnyBitIn()
    {
        ChunkCount total = ChunkCount.FromInt(16);
        Bitfield b1 = Bitfield.Empty(total).WithChunk(ChunkIndex.FromInt(0)).WithChunk(ChunkIndex.FromInt(3));
        Bitfield b2 = Bitfield.Empty(total).WithChunk(ChunkIndex.FromInt(3)).WithChunk(ChunkIndex.FromInt(7));

        Bitfield union = b1.Union(b2);
        union.PopCount.Should().Be(3);
        union.HasChunk(ChunkIndex.FromInt(0)).Should().BeTrue();
        union.HasChunk(ChunkIndex.FromInt(3)).Should().BeTrue();
        union.HasChunk(ChunkIndex.FromInt(7)).Should().BeTrue();
        union.HasChunk(ChunkIndex.FromInt(1)).Should().BeFalse();

        Bitfield required = Bitfield.Empty(total).WithChunk(ChunkIndex.FromInt(0)).WithChunk(ChunkIndex.FromInt(5));
        Bitfield localEmpty = Bitfield.Empty(total);
        Bitfield localHasZero = Bitfield.Empty(total).WithChunk(ChunkIndex.FromInt(0));

        // b1 has chunk 0 and 3. Required is 0 and 5.
        // If local is empty, b1 has chunk 0 which is in required and not in local -> true
        b1.HasAnyBitIn(required, localEmpty).Should().BeTrue();

        // If local has chunk 0, chunk 0 is already possessed, and b1 doesn't have 5 -> false
        b1.HasAnyBitIn(required, localHasZero).Should().BeFalse();

        // If peer has chunk 5
        Bitfield peerWith5 = Bitfield.Empty(total).WithChunk(ChunkIndex.FromInt(5));
        peerWith5.HasAnyBitIn(required, localHasZero).Should().BeTrue();
    }

    [Test]
    public void DT_OPT_03_SelectedFileIndices_All_SupportsHugeFileCountWithoutAllocatingHashSet()
    {
        const int count = 50_000;
        FileCount totalFiles = FileCount.FromInt(count);

        SelectedFileIndices selection = SelectedFileIndices.All(totalFiles);

        selection.SelectedCount.Should().Be(count);
        selection.Contains(FileIndex.FromInt(0)).Should().BeTrue();
        selection.Contains(FileIndex.FromInt(25_000)).Should().BeTrue();
        selection.Contains(FileIndex.FromInt(49_999)).Should().BeTrue();
        selection.Contains(FileIndex.FromInt(50_000)).Should().BeFalse();

        int enumeratedCount = 0;
        foreach (FileIndex idx in selection.Indices)
        {
            if (enumeratedCount < 5)
            {
                idx.Value.Should().Be(enumeratedCount);
            }
            enumeratedCount++;
        }
        enumeratedCount.Should().Be(count);

        // Canonical normalization: FromIndices with all elements must produce IsAll = true and equal HashCode
        FileCount smallTotal = FileCount.FromInt(3);
        SelectedFileIndices fromAll = SelectedFileIndices.All(smallTotal);
        SelectedFileIndices fromExplicit = SelectedFileIndices.FromIndices(
            [FileIndex.FromInt(0), FileIndex.FromInt(1), FileIndex.FromInt(2)], smallTotal);
        SelectedFileIndices partial = SelectedFileIndices.FromIndices(
            [FileIndex.FromInt(0), FileIndex.FromInt(1)], smallTotal);

        fromExplicit.IsAll.Should().BeTrue();
        fromExplicit.Should().Be(fromAll);
        fromExplicit.GetHashCode().Should().Be(fromAll.GetHashCode());
        partial.IsAll.Should().BeFalse();
        partial.Should().NotBe(fromAll);
    }

    [Test]
    public void DT_OPT_04_ChunkBoundaryCalculator_LinearAdvancement_Handles500TinyFilesAndZeroByteFiles()
    {
        const int numFiles = 500;
        List<FileEntry> files = new(numFiles);
        long currentOffset = 0;

        for (int i = 0; i < numFiles; i++)
        {
            // Inject a 0-byte file every 10 files
            long size = (i % 10 == 0) ? 0 : 100;
            files.Add(new FileEntry(
                FileIndex.FromInt(i),
                SanitizedRelativePath.Parse($"sub/file_{i}.bin"),
                FileSizeBytes.FromBytes(size),
                BundleByteOffset.FromOffset(currentOffset),
                FileLastModifiedUtc.FromDateTimeOffset(_testTime)));
            currentOffset += size;
        }

        ChunkSizeBytes chunkSize = ChunkSizeBytes.Default; // 1MB > currentOffset (~45 KB)
        MerkleTreeIndex tree = MerkleTreeBuilder.BuildFromLeaves([MerkleTreeBuilder.HashLeaf(new byte[currentOffset])]);
        TransferManifest manifest = TransferManifest.Create(
            TransferSessionId.New(),
            RootBundleName.Parse("TinyBundle"),
            chunkSize,
            tree,
            files);

        // All non-zero files must be contained in Chunk 0
        manifest.TotalChunks.Count.Should().Be(1);
        ChunkByteLength expectedChunkSize = manifest.GetExpectedChunkSize(ChunkIndex.FromInt(0));
        IReadOnlyList<FileChunkSlice> slices = ChunkBoundaryCalculator.CalculateSlices(manifest, ChunkIndex.FromInt(0), expectedChunkSize);

        // 50 files are 0-byte, so 450 files have non-zero slices
        slices.Count.Should().Be(450);

        long sliceSum = 0;
        foreach (FileChunkSlice slice in slices)
        {
            slice.SliceLength.Bytes.Should().Be(100);
            sliceSum += slice.SliceLength.Bytes;
        }
        sliceSum.Should().Be(currentOffset);
    }

    [Test]
    public void DT_OPT_05_RecordPreVerifiedChunk_RecordsStateAndClearsCorrectly()
    {
        TransferManifest manifest = DomainTestHelper.CreateTestManifest(fileCount: 3, bytesPerFile: 1024 * 1024);
        PublicIdentityId initiatorId = DomainTestHelper.CreateIdentity();

        TransferSession session = TransferSession.CreateIncomingStaged(
            TransferSessionId.New(),
            initiatorId,
            manifest,
            SeedRatio.Zero,
            _clock);

        session.AcceptTransfer(SelectedFileIndices.All(manifest.TotalFiles), _clock);
        session.State.Should().Be(TransferSessionState.InFlight);

        session.ClearDomainEvents();

        // Pre-verified chunk 0 recorded
        ChunkByteLength size0 = manifest.GetExpectedChunkSize(ChunkIndex.FromInt(0));
        var slices0 = ChunkBoundaryCalculator.CalculateSlices(manifest, ChunkIndex.FromInt(0), size0);
        var res0 = session.RecordPreVerifiedChunk(ChunkIndex.FromInt(0), slices0, _clock);
        res0.IsSuccess.Should().BeTrue();
        session.LocalBitfield.HasChunk(ChunkIndex.FromInt(0)).Should().BeTrue();
        session.DomainEvents.Should().ContainSingle(e => e is ChunkVerifiedDomainEvent);
        int eventCountAfterChunk0 = session.DomainEvents.Count;

        // Calling RecordPreVerifiedChunk again on chunk 0 is idempotent
        var res0Repeat = session.RecordPreVerifiedChunk(ChunkIndex.FromInt(0), slices0, _clock);
        res0Repeat.IsSuccess.Should().BeTrue();
        session.DomainEvents.Count.Should().Be(eventCountAfterChunk0);

        // Record chunks 1 and 2
        ChunkByteLength size1 = manifest.GetExpectedChunkSize(ChunkIndex.FromInt(1));
        var slices1 = ChunkBoundaryCalculator.CalculateSlices(manifest, ChunkIndex.FromInt(1), size1);
        session.RecordPreVerifiedChunk(ChunkIndex.FromInt(1), slices1, _clock);

        ChunkByteLength size2 = manifest.GetExpectedChunkSize(ChunkIndex.FromInt(2));
        var slices2 = ChunkBoundaryCalculator.CalculateSlices(manifest, ChunkIndex.FromInt(2), size2);
        session.RecordPreVerifiedChunk(ChunkIndex.FromInt(2), slices2, _clock);

        session.IsDownloadComplete.Should().BeTrue();
        session.State.Should().Be(TransferSessionState.Completed);

        // Clear domain events
        session.ClearDomainEvents();
        session.DomainEvents.Should().BeEmpty();
    }
}
