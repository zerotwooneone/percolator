using Percolator.Apps.FileTransfer.Domain.Common;

namespace Percolator.Apps.FileTransfer.Domain.ManifestManagement;

public sealed record TransferBundleSummary(
    RootBundleName RootName,
    TotalBundleBytes TotalSize,
    FileCount TotalFiles,
    ChunkSizeBytes ChunkSizeBytes,
    ChunkCount TotalChunks,
    MerkleRoot MerkleRoot)
{
    public static TransferBundleSummary Create(
        RootBundleName rootName,
        TotalBundleBytes totalSize,
        FileCount totalFiles,
        ChunkSizeBytes chunkSize,
        ChunkCount totalChunks,
        MerkleRoot merkleRoot) =>
        new(rootName, totalSize, totalFiles, chunkSize, totalChunks, merkleRoot);
}
