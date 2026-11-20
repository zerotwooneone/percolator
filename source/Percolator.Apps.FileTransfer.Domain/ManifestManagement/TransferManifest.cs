using Percolator.Apps.FileTransfer.Domain.Common;
using Percolator.Apps.FileTransfer.Domain.Exceptions;
using Percolator.Apps.FileTransfer.Domain.TransferNegotiation;
using Percolator.Domain.Common;

namespace Percolator.Apps.FileTransfer.Domain.ManifestManagement;

public sealed class TransferManifest : IEntity<TransferSessionId>
{
    public TransferSessionId Id => ManifestId;
    public TransferSessionId ManifestId { get; }
    public RootBundleName RootName { get; }
    public TotalBundleBytes TotalSize { get; }
    public FileCount TotalFiles { get; }
    public ChunkSizeBytes ChunkSize { get; }
    public ChunkCount TotalChunks { get; }
    public MerkleTreeIndex MerkleTree { get; }
    public IReadOnlyList<FileEntry> Files { get; }

    private TransferManifest(
        TransferSessionId manifestId,
        RootBundleName rootName,
        TotalBundleBytes totalSize,
        FileCount totalFiles,
        ChunkSizeBytes chunkSize,
        ChunkCount totalChunks,
        MerkleTreeIndex merkleTree,
        IReadOnlyList<FileEntry> files)
    {
        ManifestId = manifestId;
        RootName = rootName;
        TotalSize = totalSize;
        TotalFiles = totalFiles;
        ChunkSize = chunkSize;
        TotalChunks = totalChunks;
        MerkleTree = merkleTree;
        Files = files;
    }

    public static TransferManifest Create(
        TransferSessionId manifestId,
        RootBundleName rootName,
        ChunkSizeBytes chunkSize,
        MerkleTreeIndex merkleTree,
        IReadOnlyList<FileEntry> files)
    {
        ArgumentNullException.ThrowIfNull(rootName);
        ArgumentNullException.ThrowIfNull(merkleTree);

        if (files == null || files.Count == 0)
        {
            throw new DomainValidationException("Transfer bundle must contain at least one file entry.");
        }

        FileEntry[] copiedFiles = files.ToArray();

        long totalBytes = 0;
        foreach (FileEntry file in copiedFiles)
        {
            totalBytes += file.FileSize.Bytes;
        }

        if (totalBytes <= 0)
        {
            throw new DomainValidationException("Transfer bundle total byte size must be greater than zero.");
        }

        long expectedOffset = 0;
        for (int i = 0; i < copiedFiles.Length; i++)
        {
            FileEntry file = copiedFiles[i];
            if (file.Index.Value != i)
            {
                throw new DomainValidationException($"File entry at index {i} has non-sequential index {file.Index.Value}.");
            }
            if (file.StartOffset.Offset != expectedOffset)
            {
                throw new DomainValidationException($"File '{file.RelativePath.Value}' start offset ({file.StartOffset.Offset}) does not match expected contiguous bundle offset ({expectedOffset}).");
            }
            expectedOffset += file.FileSize.Bytes;
        }

        if (expectedOffset != totalBytes)
        {
            throw new DomainValidationException($"Total bundle bytes ({totalBytes}) does not equal cumulative file offsets ({expectedOffset}).");
        }

        TotalBundleBytes bundleBytes = TotalBundleBytes.FromBytes(totalBytes);
        int chunkCount = (int)Math.Max(1, (totalBytes + chunkSize.Bytes - 1) / chunkSize.Bytes);

        if (merkleTree.LeafCount != chunkCount)
        {
            throw new DomainValidationException(
                $"Merkle tree leaf count ({merkleTree.LeafCount}) must match bundle chunk count ({chunkCount}).");
        }

        return new TransferManifest(
            manifestId,
            rootName,
            bundleBytes,
            FileCount.FromInt(copiedFiles.Length),
            chunkSize,
            ChunkCount.FromInt(chunkCount),
            merkleTree,
            copiedFiles);
    }

    public ChunkByteLength GetExpectedChunkSize(ChunkIndex chunkIndex)
    {
        if (chunkIndex.Value < 0 || chunkIndex.Value >= TotalChunks.Count)
            throw new ArgumentOutOfRangeException(nameof(chunkIndex));

        long chunkStart = (long)chunkIndex.Value * ChunkSize.Bytes;
        long remainingBytes = TotalSize.Bytes - chunkStart;
        return ChunkByteLength.FromBytes((int)Math.Min((long)ChunkSize.Bytes, remainingBytes));
    }

    public Bitfield GetRequiredChunks(SelectedFileIndices selectedFiles)
    {
        ArgumentNullException.ThrowIfNull(selectedFiles);

        Bitfield required = Bitfield.Empty(TotalChunks);
        foreach (FileIndex index in selectedFiles.Indices)
        {
            FileEntry file = Files[index.Value];
            if (file.FileSize.Bytes == 0) continue;

            int firstChunk = (int)(file.StartOffset.Offset / ChunkSize.Bytes);
            int lastChunk = (int)((file.EndOffset.Offset - 1) / ChunkSize.Bytes);

            for (int c = firstChunk; c <= lastChunk; c++)
            {
                required = required.WithChunk(ChunkIndex.FromInt(c));
            }
        }
        return required;
    }
}
