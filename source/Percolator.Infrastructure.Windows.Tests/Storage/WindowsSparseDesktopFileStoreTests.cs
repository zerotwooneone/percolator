namespace Percolator.Infrastructure.Windows.Tests.Storage;

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading.Tasks;
using FluentAssertions;
using NUnit.Framework;
using Percolator.Apps.FileTransfer.Domain.Common;
using Percolator.Apps.FileTransfer.Domain.Exceptions;
using Percolator.Apps.FileTransfer.Domain.ManifestManagement;
using Percolator.Apps.FileTransfer.Domain.TransferNegotiation;
using Percolator.Apps.FileTransfer.Features.TransferNegotiation;
using Percolator.Infrastructure.Windows.Storage;

[TestFixture]
[Platform(Include = "Win", Reason = "Windows-specific storage adapter testing Win32 sparse file ioctl and path hardening.")]
public sealed class WindowsSparseDesktopFileStoreTests
{
    private string _tempRoot = null!;
    private string _tempPartials = null!;
    private WindowsSparseDesktopFileStore _fileStore = null!;

    [SetUp]
    public void SetUp()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "Percolator_WinStore_Root_" + Guid.NewGuid().ToString("N"));
        _tempPartials = Path.Combine(Path.GetTempPath(), "Percolator_WinStore_Partials_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
        Directory.CreateDirectory(_tempPartials);

        _fileStore = new WindowsSparseDesktopFileStore(_tempPartials);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_tempRoot))
        {
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }
        if (Directory.Exists(_tempPartials))
        {
            try { Directory.Delete(_tempPartials, recursive: true); } catch { }
        }
    }

    [Test]
    [TestCase("CON")]
    [TestCase("con.txt")]
    [TestCase("sub/con.tar.gz")]
    [TestCase("PRN")]
    [TestCase("prn.dat")]
    [TestCase("AUX")]
    [TestCase("aux.doc")]
    [TestCase("NUL")]
    [TestCase("nul.bin")]
    [TestCase("COM1")]
    [TestCase("com1.log")]
    [TestCase("COM9.txt")]
    [TestCase("COM¹")]
    [TestCase("COM².txt")]
    [TestCase("COM³.dat")]
    [TestCase("LPT1")]
    [TestCase("lpt9.bin")]
    [TestCase("LPT¹")]
    [TestCase("LPT³.log")]
    [TestCase("file.txt:secret")]
    [TestCase("folder:stream:$DATA")]
    [TestCase("trailing_dot.")]
    [TestCase("trailing_space ")]
    [TestCase("sub/trailing. /file.txt")]
    public void Sanitization_RejectsReservedWin32DeviceNamesAndNtfsAds(string invalidPath)
    {
        var root = RootDirectoryPath.Parse(_tempRoot);

        var act = () =>
        {
            var relPath = SanitizedRelativePath.Parse(invalidPath);
            WindowsSparseDesktopFileStore.ResolveAndValidateContainedPath(root, relPath);
        };

        act.Should().Throw<DomainValidationException>();
    }

    [Test]
    public async Task PreAllocateFilesAsync_SetsSparseAttributeOnNtfs()
    {
        var root = RootDirectoryPath.Parse(_tempRoot);
        var relPath = SanitizedRelativePath.Parse("sparse_test.bin");
        var resolved = ResolvedFilePath.Resolve(root, relPath);

        byte[] dummyLeaf = new byte[32];
        var leaf = ChunkHash.FromBytes(dummyLeaf);
        const int chunkCount = 160;
        var leaves = new List<ChunkHash>(chunkCount);
        for (int i = 0; i < chunkCount; i++)
        {
            leaves.Add(leaf);
        }
        var merkleTree = MerkleTreeIndex.Create(MerkleRoot.FromSpan(leaf.Span), leaves);

        const long expectedBytes = 10L * 1024 * 1024; // 10 MB logical file

        var fileEntry = new FileEntry(
            FileIndex.FromInt(0),
            relPath,
            FileSizeBytes.FromBytes(expectedBytes),
            BundleByteOffset.Zero,
            FileLastModifiedUtc.FromDateTimeOffset(DateTimeOffset.UtcNow));

        var manifest = TransferManifest.Create(
            TransferSessionId.New(),
            RootBundleName.Parse("SparseBundle"),
            ChunkSizeBytes.FromBytes(65536),
            merkleTree,
            [fileEntry]);

        await _fileStore.PreAllocateFilesAsync(root, manifest, SelectedFileIndices.All(manifest.TotalFiles));

        string partPath = $"{resolved.AbsolutePath}.percolator-part";
        File.Exists(partPath).Should().BeTrue();

        var fileInfo = new FileInfo(partPath);
        fileInfo.Length.Should().Be(expectedBytes);

        // Check if drive supports sparse files (NTFS/ReFS)
        string driveRoot = Path.GetPathRoot(_tempRoot) ?? _tempRoot;
        var drive = new DriveInfo(driveRoot);
        if (string.Equals(drive.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(drive.DriveFormat, "ReFS", StringComparison.OrdinalIgnoreCase))
        {
            (fileInfo.Attributes & FileAttributes.SparseFile).Should().NotBe(0, "File should have SparseFile attribute set on NTFS/ReFS");
        }
    }

    [Test]
    public async Task HappyPath_WriteAndReadChunk_MatchesExactBytes()
    {
        var root = RootDirectoryPath.Parse(_tempRoot);
        var relPath = SanitizedRelativePath.Parse("media/large_video.bin");
        var resolved = ResolvedFilePath.Resolve(root, relPath);

        byte[] payload1 = new byte[65536];
        byte[] payload2 = new byte[65536];
        RandomNumberGenerator.Fill(payload1);
        RandomNumberGenerator.Fill(payload2);

        var fileEntry = new FileEntry(
            FileIndex.FromInt(0),
            relPath,
            FileSizeBytes.FromBytes(131072),
            BundleByteOffset.Zero,
            FileLastModifiedUtc.FromDateTimeOffset(DateTimeOffset.UtcNow));

        var leaf1 = MerkleTreeBuilder.HashLeaf(payload1);
        var leaf2 = MerkleTreeBuilder.HashLeaf(payload2);
        var merkleTree = MerkleTreeIndex.Create(MerkleRoot.FromSpan(leaf1.Span), [leaf1, leaf2]);

        var manifest = TransferManifest.Create(
            TransferSessionId.New(),
            RootBundleName.Parse("HappyBundle"),
            ChunkSizeBytes.FromBytes(65536),
            merkleTree,
            [fileEntry]);

        // Pre-allocate
        await _fileStore.PreAllocateFilesAsync(root, manifest, SelectedFileIndices.All(manifest.TotalFiles));

        string partPath = $"{resolved.AbsolutePath}.percolator-part";
        File.Exists(partPath).Should().BeTrue();

        // Write chunk 0 and chunk 1 concurrently using RandomAccess
        Task writeTask0 = _fileStore.WriteChunkAsync(
            resolved,
            FileByteOffset.Zero,
            PlaintextChunkData.FromBytes(payload1)).AsTask();

        Task writeTask1 = _fileStore.WriteChunkAsync(
            resolved,
            FileByteOffset.FromOffset(65536),
            PlaintextChunkData.FromBytes(payload2)).AsTask();

        await Task.WhenAll(writeTask0, writeTask1);

        // Read chunks back
        PlaintextChunkData read0 = await _fileStore.ReadChunkAsync(
            resolved,
            FileByteOffset.Zero,
            TransferByteLength.FromBytes(65536));

        PlaintextChunkData read1 = await _fileStore.ReadChunkAsync(
            resolved,
            FileByteOffset.FromOffset(65536),
            TransferByteLength.FromBytes(65536));

        read0.Span.SequenceEqual(payload1).Should().BeTrue();
        read1.Span.SequenceEqual(payload2).Should().BeTrue();

        // Finalize file
        var timestamp = FileLastModifiedUtc.FromDateTimeOffset(DateTimeOffset.UtcNow.AddHours(-1));
        await _fileStore.FinalizeFileAsync(resolved, timestamp);

        File.Exists(resolved.AbsolutePath).Should().BeTrue();
        File.Exists(partPath).Should().BeFalse();

        // Read chunk from finalized file
        PlaintextChunkData readAfterFinalize = await _fileStore.ReadChunkAsync(
            resolved,
            FileByteOffset.Zero,
            TransferByteLength.FromBytes(65536));

        readAfterFinalize.Span.SequenceEqual(payload1).Should().BeTrue();
    }

    [Test]
    public async Task BoundarySlice_WriteAndReadForUnselectedFile_PersistsAndCleansUp()
    {
        var sessionId = TransferSessionId.New();
        var chunkIndex = ChunkIndex.FromInt(5);
        var fileIndex = FileIndex.FromInt(2);

        byte[] sliceData = new byte[2048];
        RandomNumberGenerator.Fill(sliceData);

        await _fileStore.WriteBoundarySliceAsync(
            sessionId,
            chunkIndex,
            fileIndex,
            PlaintextChunkData.FromBytes(sliceData));

        PlaintextChunkData? readSlice = await _fileStore.ReadBoundarySliceAsync(
            sessionId,
            chunkIndex,
            fileIndex,
            TransferByteLength.FromBytes(2048));

        readSlice.Should().NotBeNull();
        readSlice!.Span.SequenceEqual(sliceData).Should().BeTrue();

        await _fileStore.DeleteBoundaryPartsAsync(sessionId);

        PlaintextChunkData? readAfterDelete = await _fileStore.ReadBoundarySliceAsync(
            sessionId,
            chunkIndex,
            fileIndex,
            TransferByteLength.FromBytes(2048));

        readAfterDelete.Should().BeNull();
    }

    [Test]
    public async Task ConcurrentWriteChunkAndScanBitfield_VerifiesMerkleLeaves()
    {
        var root = RootDirectoryPath.Parse(_tempRoot);
        var relPath = SanitizedRelativePath.Parse("bitfield_test.dat");
        var resolved = ResolvedFilePath.Resolve(root, relPath);

        byte[] chunk0 = new byte[65536];
        byte[] chunk1 = new byte[65536];
        RandomNumberGenerator.Fill(chunk0);
        RandomNumberGenerator.Fill(chunk1);

        var leaf0 = MerkleTreeBuilder.HashLeaf(chunk0);
        var leaf1 = MerkleTreeBuilder.HashLeaf(chunk1);
        var merkleTree = MerkleTreeIndex.Create(MerkleRoot.FromSpan(leaf0.Span), [leaf0, leaf1]);

        var fileEntry = new FileEntry(
            FileIndex.FromInt(0),
            relPath,
            FileSizeBytes.FromBytes(131072),
            BundleByteOffset.Zero,
            FileLastModifiedUtc.FromDateTimeOffset(DateTimeOffset.UtcNow));

        var manifest = TransferManifest.Create(
            TransferSessionId.New(),
            RootBundleName.Parse("BitfieldBundle"),
            ChunkSizeBytes.FromBytes(65536),
            merkleTree,
            [fileEntry]);

        await _fileStore.PreAllocateFilesAsync(root, manifest, SelectedFileIndices.All(manifest.TotalFiles));

        // Write only chunk 1 first (out of order)
        await _fileStore.WriteChunkAsync(resolved, FileByteOffset.FromOffset(65536), PlaintextChunkData.FromBytes(chunk1));

        Bitfield initialBitfield = await _fileStore.ScanAndComputeBitfieldAsync(root, manifest);
        initialBitfield.HasChunk(ChunkIndex.FromInt(0)).Should().BeFalse();
        initialBitfield.HasChunk(ChunkIndex.FromInt(1)).Should().BeTrue();
        initialBitfield.IsComplete.Should().BeFalse();

        // Write chunk 0
        await _fileStore.WriteChunkAsync(resolved, FileByteOffset.Zero, PlaintextChunkData.FromBytes(chunk0));

        Bitfield fullBitfield = await _fileStore.ScanAndComputeBitfieldAsync(root, manifest);
        fullBitfield.HasChunk(ChunkIndex.FromInt(0)).Should().BeTrue();
        fullBitfield.HasChunk(ChunkIndex.FromInt(1)).Should().BeTrue();
        fullBitfield.IsComplete.Should().BeTrue();
    }
}
