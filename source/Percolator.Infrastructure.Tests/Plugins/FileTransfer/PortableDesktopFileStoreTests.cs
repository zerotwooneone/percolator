namespace Percolator.Infrastructure.Tests.Plugins.FileTransfer;

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
using Percolator.Infrastructure.Plugins.FileTransfer;

[TestFixture]
public sealed class PortableDesktopFileStoreTests
{
    private string _tempRoot = null!;
    private string _tempPartials = null!;
    private PortableDesktopFileStore _fileStore = null!;

    [SetUp]
    public void SetUp()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "Percolator_FileStore_Root_" + Guid.NewGuid().ToString("N"));
        _tempPartials = Path.Combine(Path.GetTempPath(), "Percolator_FileStore_Partials_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
        Directory.CreateDirectory(_tempPartials);

        _fileStore = new PortableDesktopFileStore(_tempPartials);
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
    public void PathValidation_RejectsDirectoryTraversalAndRootEscape()
    {
        var root = RootDirectoryPath.Parse(_tempRoot);

        // Path containing .. segment is rejected by SanitizedRelativePath
        var act1 = () => SanitizedRelativePath.Parse("folder/../escape.txt");
        act1.Should().Throw<DomainValidationException>();

        // Path starting with separator is rejected
        var act2 = () => SanitizedRelativePath.Parse("/etc/shadow");
        act2.Should().Throw<DomainValidationException>();

        // Valid relative path resolves inside canonical root
        var validPath = SanitizedRelativePath.Parse("subfolder/doc.txt");
        var resolved = PortableDesktopFileStore.ResolveAndValidateContainedPath(root, validPath);
        resolved.AbsolutePath.Should().StartWith(root.Value);
    }

    [Test]
    public async Task ZeroByteFile_MaterializesImmediately()
    {
        var root = RootDirectoryPath.Parse(_tempRoot);

        byte[] nonZeroPayload = new byte[1024];
        RandomNumberGenerator.Fill(nonZeroPayload);

        var zeroByteEntry = new FileEntry(
            FileIndex.FromInt(0),
            SanitizedRelativePath.Parse("zero.dat"),
            FileSizeBytes.FromBytes(0),
            BundleByteOffset.Zero,
            FileLastModifiedUtc.FromDateTimeOffset(DateTimeOffset.UtcNow));

        var nonZeroEntry = new FileEntry(
            FileIndex.FromInt(1),
            SanitizedRelativePath.Parse("content.dat"),
            FileSizeBytes.FromBytes(1024),
            BundleByteOffset.Zero,
            FileLastModifiedUtc.FromDateTimeOffset(DateTimeOffset.UtcNow));

        var leaf = MerkleTreeBuilder.HashLeaf(nonZeroPayload);
        var merkleTree = MerkleTreeIndex.Create(MerkleRoot.FromSpan(leaf.Span), [leaf]);
        var manifest = TransferManifest.Create(
            TransferSessionId.New(),
            RootBundleName.Parse("TestBundle"),
            ChunkSizeBytes.FromBytes(65536),
            merkleTree,
            [zeroByteEntry, nonZeroEntry]);

        await _fileStore.PreAllocateFilesAsync(root, manifest, SelectedFileIndices.All(manifest.TotalFiles));

        string zeroTargetPath = Path.Combine(_tempRoot, "zero.dat");
        File.Exists(zeroTargetPath).Should().BeTrue();
        new FileInfo(zeroTargetPath).Length.Should().Be(0);
        File.Exists($"{zeroTargetPath}.percolator-part").Should().BeFalse();

        string contentPartPath = Path.Combine(_tempRoot, "content.dat.percolator-part");
        File.Exists(contentPartPath).Should().BeTrue();
        new FileInfo(contentPartPath).Length.Should().Be(1024);
    }

    [Test]
    public async Task HappyPath_WriteAndReadChunk_MatchesExactBytes()
    {
        var root = RootDirectoryPath.Parse(_tempRoot);
        var relPath = SanitizedRelativePath.Parse("media/test.bin");
        var resolved = ResolvedFilePath.Resolve(root, relPath);

        byte[] payload = new byte[65536];
        RandomNumberGenerator.Fill(payload);

        // Pre-allocate 128 KB file
        var fileEntry = new FileEntry(
            FileIndex.FromInt(0),
            relPath,
            FileSizeBytes.FromBytes(131072),
            BundleByteOffset.Zero,
            FileLastModifiedUtc.FromDateTimeOffset(DateTimeOffset.UtcNow));

        var leaves = new List<ChunkHash>
        {
            MerkleTreeBuilder.HashLeaf(payload),
            MerkleTreeBuilder.HashLeaf(payload)
        };
        var merkleTree = MerkleTreeIndex.Create(MerkleRoot.FromSpan(leaves[0].Span), leaves);
        var manifest = TransferManifest.Create(
            TransferSessionId.New(),
            RootBundleName.Parse("TestBundle"),
            ChunkSizeBytes.FromBytes(65536),
            merkleTree,
            [fileEntry]);

        await _fileStore.PreAllocateFilesAsync(root, manifest, SelectedFileIndices.All(manifest.TotalFiles));

        // Part file exists with length 131072
        string partPath = $"{resolved.AbsolutePath}.percolator-part";
        File.Exists(partPath).Should().BeTrue();
        new FileInfo(partPath).Length.Should().Be(131072);

        // Write first chunk to part file
        await _fileStore.WriteChunkAsync(
            resolved,
            FileByteOffset.Zero,
            PlaintextChunkData.FromBytes(payload));

        // Read chunk back
        PlaintextChunkData readChunk = await _fileStore.ReadChunkAsync(
            resolved,
            FileByteOffset.Zero,
            TransferByteLength.FromBytes(65536));

        readChunk.Span.SequenceEqual(payload).Should().BeTrue();

        // Finalize file
        var modified = FileLastModifiedUtc.FromDateTimeOffset(DateTimeOffset.UtcNow.AddMinutes(-10));
        await _fileStore.FinalizeFileAsync(resolved, modified);

        File.Exists(resolved.AbsolutePath).Should().BeTrue();
        File.Exists(partPath).Should().BeFalse();

        // Read chunk from finalized file
        PlaintextChunkData readAfterFinalize = await _fileStore.ReadChunkAsync(
            resolved,
            FileByteOffset.Zero,
            TransferByteLength.FromBytes(65536));

        readAfterFinalize.Span.SequenceEqual(payload).Should().BeTrue();
    }

    [Test]
    public async Task BoundarySlice_WriteAndReadForUnselectedFile_PersistsAndCleansUp()
    {
        var sessionId = TransferSessionId.New();
        var chunkIndex = ChunkIndex.FromInt(3);
        var fileIndex = FileIndex.FromInt(1);

        byte[] sliceBytes = new byte[1024];
        RandomNumberGenerator.Fill(sliceBytes);

        // Write boundary slice
        await _fileStore.WriteBoundarySliceAsync(
            sessionId,
            chunkIndex,
            fileIndex,
            PlaintextChunkData.FromBytes(sliceBytes));

        // Read slice back
        PlaintextChunkData? readSlice = await _fileStore.ReadBoundarySliceAsync(
            sessionId,
            chunkIndex,
            fileIndex,
            TransferByteLength.FromBytes(1024));

        readSlice.Should().NotBeNull();
        readSlice!.Span.SequenceEqual(sliceBytes).Should().BeTrue();

        // Delete boundary parts
        await _fileStore.DeleteBoundaryPartsAsync(sessionId);

        PlaintextChunkData? readAfterDelete = await _fileStore.ReadBoundarySliceAsync(
            sessionId,
            chunkIndex,
            fileIndex,
            TransferByteLength.FromBytes(1024));

        readAfterDelete.Should().BeNull();
    }

    [Test]
    public async Task BitfieldScanner_MatchesMerkleLeaves()
    {
        var root = RootDirectoryPath.Parse(_tempRoot);
        var relPath = SanitizedRelativePath.Parse("chunked.bin");
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
            RootBundleName.Parse("TestBundle"),
            ChunkSizeBytes.FromBytes(65536),
            merkleTree,
            [fileEntry]);

        // Pre-allocate
        await _fileStore.PreAllocateFilesAsync(root, manifest, SelectedFileIndices.All(manifest.TotalFiles));

        // Write only chunk 0
        await _fileStore.WriteChunkAsync(resolved, FileByteOffset.Zero, PlaintextChunkData.FromBytes(chunk0));

        // Scan bitfield - only chunk 0 should match
        Bitfield partialBitfield = await _fileStore.ScanAndComputeBitfieldAsync(root, manifest);
        partialBitfield.HasChunk(ChunkIndex.FromInt(0)).Should().BeTrue();
        partialBitfield.HasChunk(ChunkIndex.FromInt(1)).Should().BeFalse();

        // Write chunk 1
        await _fileStore.WriteChunkAsync(resolved, FileByteOffset.FromOffset(65536), PlaintextChunkData.FromBytes(chunk1));

        // Scan bitfield again - both should match
        Bitfield fullBitfield = await _fileStore.ScanAndComputeBitfieldAsync(root, manifest);
        fullBitfield.HasChunk(ChunkIndex.FromInt(0)).Should().BeTrue();
        fullBitfield.HasChunk(ChunkIndex.FromInt(1)).Should().BeTrue();
        fullBitfield.IsComplete.Should().BeTrue();
    }
}
