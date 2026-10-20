using Percolator.Apps.FileTransfer.Domain.Common;
using Percolator.Apps.FileTransfer.Domain.ManifestManagement;
using Percolator.Apps.FileTransfer.Domain.TransferNegotiation;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Apps.FileTransfer.Domain.Tests;

public static class DomainTestHelper
{
    public static TransferManifest CreateTestManifest(
        int fileCount = 1,
        long bytesPerFile = 1024 * 1024,
        int chunkSizeBytes = 1024 * 1024)
    {
        TransferSessionId manifestId = TransferSessionId.New();
        RootBundleName rootName = RootBundleName.Parse("TestBundle");
        ChunkSizeBytes chunkSize = ChunkSizeBytes.FromBytes(chunkSizeBytes);

        long totalBytes = (long)fileCount * bytesPerFile;
        int totalChunks = (int)Math.Max(1, (totalBytes + chunkSize.Bytes - 1) / chunkSize.Bytes);

        List<ChunkHash> leaves = [];
        for (int i = 0; i < totalChunks; i++)
        {
            byte[] chunkData = new byte[chunkSizeBytes];
            // Write chunk index into first 4 bytes to make chunk hashes unique
            BitConverter.GetBytes(i).CopyTo(chunkData, 0);
            leaves.Add(MerkleTreeBuilder.HashLeaf(chunkData));
        }

        MerkleTreeIndex merkleTree = MerkleTreeBuilder.BuildFromLeaves(leaves);

        List<FileEntry> files = [];
        long currentOffset = 0;
        DateTimeOffset now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        for (int i = 0; i < fileCount; i++)
        {
            files.Add(new FileEntry(
                FileIndex.FromInt(i),
                SanitizedRelativePath.Parse($"file_{i}.dat"),
                FileSizeBytes.FromBytes(bytesPerFile),
                BundleByteOffset.FromOffset(currentOffset),
                FileLastModifiedUtc.FromDateTimeOffset(now)));

            currentOffset += bytesPerFile;
        }

        return TransferManifest.Create(manifestId, rootName, chunkSize, merkleTree, files);
    }

    public static PublicIdentityId CreateIdentity() => PublicIdentityId.New();
}
