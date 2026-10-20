using Percolator.Apps.FileTransfer.Domain.Common;
using Percolator.Apps.FileTransfer.Domain.Exceptions;
using Percolator.Apps.FileTransfer.Domain.ManifestManagement;
using Percolator.Apps.FileTransfer.Domain.TransferNegotiation;

namespace Percolator.Apps.FileTransfer.Domain.Tests;

[TestFixture]
public sealed class ManifestTests
{
    private DateTimeOffset _testTime;

    [SetUp]
    public void SetUp()
    {
        _testTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    }

    [Test]
    public void DT_MAN_01_EmptyBundleRejection_ThrowsDomainValidationException()
    {
        TransferSessionId manifestId = TransferSessionId.New();
        RootBundleName rootName = RootBundleName.Parse("EmptyBundle");
        ChunkSizeBytes chunkSize = ChunkSizeBytes.Default;
        MerkleTreeIndex merkleTree = MerkleTreeBuilder.BuildFromLeaves([MerkleTreeBuilder.HashLeaf([0x01])]);

        Action act = () => TransferManifest.Create(manifestId, rootName, chunkSize, merkleTree, []);
        act.Should().Throw<DomainValidationException>();
    }

    [Test]
    public void DT_MAN_02_ContiguousOffsetValidation_GapsThrowException()
    {
        TransferSessionId manifestId = TransferSessionId.New();
        RootBundleName rootName = RootBundleName.Parse("GapBundle");
        ChunkSizeBytes chunkSize = ChunkSizeBytes.Default;

        // File 0: 1000 bytes, starts at 0
        FileEntry file0 = new(
            FileIndex.FromInt(0),
            SanitizedRelativePath.Parse("file0.bin"),
            FileSizeBytes.FromBytes(1000),
            BundleByteOffset.FromOffset(0),
            FileLastModifiedUtc.FromDateTimeOffset(_testTime));

        // File 1: 1000 bytes, starts at 1001 (gap of 1 byte)
        FileEntry file1 = new(
            FileIndex.FromInt(1),
            SanitizedRelativePath.Parse("file1.bin"),
            FileSizeBytes.FromBytes(1000),
            BundleByteOffset.FromOffset(1001),
            FileLastModifiedUtc.FromDateTimeOffset(_testTime));

        MerkleTreeIndex merkleTree = MerkleTreeBuilder.BuildFromLeaves([MerkleTreeBuilder.HashLeaf([0x01])]);

        Action act = () => TransferManifest.Create(manifestId, rootName, chunkSize, merkleTree, [file0, file1]);
        act.Should().Throw<DomainValidationException>();
    }

    [Test]
    public void DT_MAN_03_CumulativeFileSizeEqualityInvariant_DetectsInconsistency()
    {
        TransferSessionId manifestId = TransferSessionId.New();
        RootBundleName rootName = RootBundleName.Parse("OverlapBundle");
        ChunkSizeBytes chunkSize = ChunkSizeBytes.Default;

        // Non-sequential index: file 0 has index 1
        FileEntry file0 = new(
            FileIndex.FromInt(1),
            SanitizedRelativePath.Parse("file0.bin"),
            FileSizeBytes.FromBytes(1000),
            BundleByteOffset.FromOffset(0),
            FileLastModifiedUtc.FromDateTimeOffset(_testTime));

        MerkleTreeIndex merkleTree = MerkleTreeBuilder.BuildFromLeaves([MerkleTreeBuilder.HashLeaf([0x01])]);

        Action act = () => TransferManifest.Create(manifestId, rootName, chunkSize, merkleTree, [file0]);
        act.Should().Throw<DomainValidationException>();
    }

    [Test]
    public void DT_MAN_04_SubChunkBundleLayoutMath_SingleChunkExpectedSize()
    {
        TransferSessionId manifestId = TransferSessionId.New();
        RootBundleName rootName = RootBundleName.Parse("SmallBundle");
        ChunkSizeBytes chunkSize = ChunkSizeBytes.Default; // 1 MB
        const long fileSize = 500;

        FileEntry file = new(
            FileIndex.FromInt(0),
            SanitizedRelativePath.Parse("small.bin"),
            FileSizeBytes.FromBytes(fileSize),
            BundleByteOffset.Zero,
            FileLastModifiedUtc.FromDateTimeOffset(_testTime));

        MerkleTreeIndex merkleTree = MerkleTreeBuilder.BuildFromLeaves([MerkleTreeBuilder.HashLeaf(new byte[fileSize])]);

        TransferManifest manifest = TransferManifest.Create(manifestId, rootName, chunkSize, merkleTree, [file]);

        manifest.TotalChunks.Count.Should().Be(1);
        manifest.GetExpectedChunkSize(ChunkIndex.FromInt(0)).Bytes.Should().Be((int)fileSize);
    }

    [Test]
    public void DT_MAN_05_ChunkBoundarySpanning_AcrossTwoAdjacentFiles()
    {
        // 1 MB chunk (1,048,576 bytes)
        int chunkSize = 1024 * 1024;
        long file0Size = 600 * 1024;
        long file1Size = 800 * 1024;

        FileEntry file0 = new(
            FileIndex.FromInt(0),
            SanitizedRelativePath.Parse("file0.bin"),
            FileSizeBytes.FromBytes(file0Size),
            BundleByteOffset.FromOffset(0),
            FileLastModifiedUtc.FromDateTimeOffset(_testTime));

        FileEntry file1 = new(
            FileIndex.FromInt(1),
            SanitizedRelativePath.Parse("file1.bin"),
            FileSizeBytes.FromBytes(file1Size),
            BundleByteOffset.FromOffset(file0Size),
            FileLastModifiedUtc.FromDateTimeOffset(_testTime));

        MerkleTreeIndex merkleTree = MerkleTreeBuilder.BuildFromLeaves([
            MerkleTreeBuilder.HashLeaf(new byte[chunkSize]),
            MerkleTreeBuilder.HashLeaf(new byte[(file0Size + file1Size) - chunkSize])
        ]);

        TransferManifest manifest = TransferManifest.Create(
            TransferSessionId.New(),
            RootBundleName.Parse("SpanningBundle"),
            ChunkSizeBytes.FromBytes(chunkSize),
            merkleTree,
            [file0, file1]);

        // Act: Calculate slices for chunk 0 (1 MB)
        IReadOnlyList<FileChunkSlice> slices = ChunkBoundaryCalculator.CalculateSlices(
            manifest,
            ChunkIndex.FromInt(0),
            ChunkByteLength.FromBytes(chunkSize));

        // Assert: 2 slices
        slices.Should().HaveCount(2);

        // Slice 0: File 0
        slices[0].FileIndex.Value.Should().Be(0);
        slices[0].OffsetInFile.Offset.Should().Be(0);
        slices[0].SliceLength.Bytes.Should().Be((int)file0Size);
        slices[0].BufferOffset.Should().Be(0);

        // Slice 1: File 1
        slices[1].FileIndex.Value.Should().Be(1);
        slices[1].OffsetInFile.Offset.Should().Be(0);
        slices[1].SliceLength.Bytes.Should().Be((int)(chunkSize - file0Size));
        slices[1].BufferOffset.Should().Be((int)file0Size);
    }

    [Test]
    public void DT_MAN_06_ChunkBoundarySpanning_WithInterleavedZeroByteFiles()
    {
        int chunkSize = 1024 * 1024;
        long file0Size = 500 * 1024;
        long file1Size = 0; // zero-byte file
        long file2Size = 800 * 1024;

        FileEntry file0 = new(
            FileIndex.FromInt(0),
            SanitizedRelativePath.Parse("file0.bin"),
            FileSizeBytes.FromBytes(file0Size),
            BundleByteOffset.FromOffset(0),
            FileLastModifiedUtc.FromDateTimeOffset(_testTime));

        FileEntry file1 = new(
            FileIndex.FromInt(1),
            SanitizedRelativePath.Parse("file1_empty.bin"),
            FileSizeBytes.FromBytes(file1Size),
            BundleByteOffset.FromOffset(file0Size),
            FileLastModifiedUtc.FromDateTimeOffset(_testTime));

        FileEntry file2 = new(
            FileIndex.FromInt(2),
            SanitizedRelativePath.Parse("file2.bin"),
            FileSizeBytes.FromBytes(file2Size),
            BundleByteOffset.FromOffset(file0Size),
            FileLastModifiedUtc.FromDateTimeOffset(_testTime));

        MerkleTreeIndex merkleTree = MerkleTreeBuilder.BuildFromLeaves([
            MerkleTreeBuilder.HashLeaf(new byte[chunkSize]),
            MerkleTreeBuilder.HashLeaf(new byte[(file0Size + file2Size) - chunkSize])
        ]);

        TransferManifest manifest = TransferManifest.Create(
            TransferSessionId.New(),
            RootBundleName.Parse("ZeroByteInterleaved"),
            ChunkSizeBytes.FromBytes(chunkSize),
            merkleTree,
            [file0, file1, file2]);

        // Act: Slices for chunk 0
        IReadOnlyList<FileChunkSlice> slices = ChunkBoundaryCalculator.CalculateSlices(
            manifest,
            ChunkIndex.FromInt(0),
            ChunkByteLength.FromBytes(chunkSize));

        // Assert: Slices only for File 0 and File 2; File 1 is skipped
        slices.Should().HaveCount(2);
        slices[0].FileIndex.Value.Should().Be(0);
        slices[1].FileIndex.Value.Should().Be(2);
    }

    [Test]
    public void DT_MAN_07_SelectiveDownload_RequiredChunkBitfieldCalculation()
    {
        // 10 files, 1 MB each, 1 MB chunks -> 10 chunks total
        TransferManifest manifest = DomainTestHelper.CreateTestManifest(fileCount: 10, bytesPerFile: 1024 * 1024);

        SelectedFileIndices selection = SelectedFileIndices.FromIndices(
            [FileIndex.FromInt(2), FileIndex.FromInt(5)],
            manifest.TotalFiles);

        // Act
        Bitfield required = manifest.GetRequiredChunks(selection);

        // Assert
        required.PopCount.Should().Be(2);
        required.HasChunk(ChunkIndex.FromInt(2)).Should().BeTrue();
        required.HasChunk(ChunkIndex.FromInt(5)).Should().BeTrue();
        required.HasChunk(ChunkIndex.FromInt(0)).Should().BeFalse();
        required.HasChunk(ChunkIndex.FromInt(1)).Should().BeFalse();
        required.HasChunk(ChunkIndex.FromInt(3)).Should().BeFalse();
        required.HasChunk(ChunkIndex.FromInt(4)).Should().BeFalse();
        required.HasChunk(ChunkIndex.FromInt(6)).Should().BeFalse();
        required.HasChunk(ChunkIndex.FromInt(7)).Should().BeFalse();
        required.HasChunk(ChunkIndex.FromInt(8)).Should().BeFalse();
        required.HasChunk(ChunkIndex.FromInt(9)).Should().BeFalse();
    }

    [TestCase("../../etc/passwd")]
    [TestCase("..\\..\\windows\\system32")]
    [TestCase("/absolute/path.txt")]
    [TestCase("C:/file.txt")]
    [TestCase("./local/file.txt")]
    [TestCase("folder/../escape.txt")]
    [TestCase("")]
    [TestCase("   ")]
    public void DT_MAN_08_RelativePath_DirectoryTraversalSanitization_RejectsMaliciousPaths(string rawPath)
    {
        Action act = () => SanitizedRelativePath.Parse(rawPath);
        act.Should().Throw<DomainValidationException>();
    }

    [Test]
    public void DT_MAN_09_MultiChunk_SingleFileBoundaryCalculations()
    {
        int chunkSize = 1024 * 1024; // 1 MB
        long fileSize = (long)(2.5 * chunkSize); // 2.5 MB -> 3 chunks

        FileEntry file = new(
            FileIndex.FromInt(0),
            SanitizedRelativePath.Parse("large.dat"),
            FileSizeBytes.FromBytes(fileSize),
            BundleByteOffset.Zero,
            FileLastModifiedUtc.FromDateTimeOffset(_testTime));

        MerkleTreeIndex merkleTree = MerkleTreeBuilder.BuildFromLeaves([
            MerkleTreeBuilder.HashLeaf(new byte[chunkSize]),
            MerkleTreeBuilder.HashLeaf(new byte[chunkSize]),
            MerkleTreeBuilder.HashLeaf(new byte[chunkSize / 2])
        ]);

        TransferManifest manifest = TransferManifest.Create(
            TransferSessionId.New(),
            RootBundleName.Parse("LargeBundle"),
            ChunkSizeBytes.FromBytes(chunkSize),
            merkleTree,
            [file]);

        manifest.TotalChunks.Count.Should().Be(3);

        // Chunk 0: offset 0, 1 MB
        IReadOnlyList<FileChunkSlice> slice0 = ChunkBoundaryCalculator.CalculateSlices(
            manifest, ChunkIndex.FromInt(0), ChunkByteLength.FromBytes(chunkSize));
        slice0.Should().ContainSingle();
        slice0[0].OffsetInFile.Offset.Should().Be(0);
        slice0[0].SliceLength.Bytes.Should().Be(chunkSize);

        // Chunk 1: offset 1 MB, 1 MB
        IReadOnlyList<FileChunkSlice> slice1 = ChunkBoundaryCalculator.CalculateSlices(
            manifest, ChunkIndex.FromInt(1), ChunkByteLength.FromBytes(chunkSize));
        slice1.Should().ContainSingle();
        slice1[0].OffsetInFile.Offset.Should().Be(chunkSize);
        slice1[0].SliceLength.Bytes.Should().Be(chunkSize);

        // Chunk 2: offset 2 MB, 0.5 MB
        IReadOnlyList<FileChunkSlice> slice2 = ChunkBoundaryCalculator.CalculateSlices(
            manifest, ChunkIndex.FromInt(2), ChunkByteLength.FromBytes(chunkSize / 2));
        slice2.Should().ContainSingle();
        slice2[0].OffsetInFile.Offset.Should().Be(2 * chunkSize);
        slice2[0].SliceLength.Bytes.Should().Be(chunkSize / 2);
    }

    [Test]
    public void DT_MAN_10_AllZeroByteBundleRejection_ThrowsDomainValidationException()
    {
        FileEntry file0 = new(
            FileIndex.FromInt(0),
            SanitizedRelativePath.Parse("zero0.dat"),
            FileSizeBytes.FromBytes(0),
            BundleByteOffset.Zero,
            FileLastModifiedUtc.FromDateTimeOffset(_testTime));

        FileEntry file1 = new(
            FileIndex.FromInt(1),
            SanitizedRelativePath.Parse("zero1.dat"),
            FileSizeBytes.FromBytes(0),
            BundleByteOffset.Zero,
            FileLastModifiedUtc.FromDateTimeOffset(_testTime));

        MerkleTreeIndex merkleTree = MerkleTreeBuilder.BuildFromLeaves([MerkleTreeBuilder.HashLeaf([0x00])]);

        Action act = () => TransferManifest.Create(
            TransferSessionId.New(),
            RootBundleName.Parse("ZeroBundle"),
            ChunkSizeBytes.Default,
            merkleTree,
            [file0, file1]);

        act.Should().Throw<DomainValidationException>()
            .WithMessage("*total byte size must be greater than zero*");
    }

    [Test]
    public void DT_MAN_11_SelectedFileIndices_OutOfBoundsRejection_ThrowsException()
    {
        FileCount fileCount = FileCount.FromInt(3);

        Action actNegative = () => SelectedFileIndices.FromIndices([FileIndex.FromInt(-1)], fileCount);
        actNegative.Should().Throw<DomainValidationException>();

        Action actOver = () => SelectedFileIndices.FromIndices([FileIndex.FromInt(3)], fileCount);
        actOver.Should().Throw<DomainValidationException>();

        Action actEmpty = () => SelectedFileIndices.FromIndices([], fileCount);
        actEmpty.Should().Throw<DomainValidationException>();
    }
}
