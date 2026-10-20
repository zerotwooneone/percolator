using System.Security.Cryptography;
using Percolator.Apps.FileTransfer.Domain.Exceptions;
using Percolator.Apps.FileTransfer.Domain.TransferNegotiation;

namespace Percolator.Apps.FileTransfer.Domain.ManifestManagement;

public static class MerkleTreeBuilder
{
    public static ChunkHash HashLeaf(ReadOnlySpan<byte> plaintextChunk)
    {
        byte[] buffer = new byte[1 + plaintextChunk.Length];
        buffer[0] = 0x00; // Leaf domain separation prefix
        plaintextChunk.CopyTo(buffer.AsSpan(1));

        byte[] hash = SHA256.HashData(buffer);
        return ChunkHash.FromBytes(hash);
    }

    public static byte[] HashInterior(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
    {
        byte[] buffer = new byte[1 + left.Length + right.Length];
        buffer[0] = 0x01; // Interior domain separation prefix
        left.CopyTo(buffer.AsSpan(1));
        right.CopyTo(buffer.AsSpan(1 + left.Length));

        return SHA256.HashData(buffer);
    }

    public static MerkleTreeIndex BuildFromLeaves(IReadOnlyList<ChunkHash> leaves)
    {
        if (leaves == null || leaves.Count == 0)
            throw new DomainValidationException("Cannot build Merkle tree from empty leaf collection.");

        if (leaves.Count == 1)
        {
            MerkleRoot singleRoot = MerkleRoot.FromBytes(leaves[0].Span.ToArray());
            return MerkleTreeIndex.Create(singleRoot, leaves);
        }

        List<byte[]> currentLevel = new(leaves.Count);
        for (int i = 0; i < leaves.Count; i++)
        {
            currentLevel.Add(leaves[i].Span.ToArray());
        }

        while (currentLevel.Count > 1)
        {
            int nextLevelCount = (currentLevel.Count + 1) / 2;
            List<byte[]> nextLevel = new(nextLevelCount);

            for (int i = 0; i < currentLevel.Count; i += 2)
            {
                if (i + 1 < currentLevel.Count)
                {
                    byte[] parent = HashInterior(currentLevel[i], currentLevel[i + 1]);
                    nextLevel.Add(parent);
                }
                else
                {
                    // Odd-node promotion rule: promote unpaired node directly without duplicate hashing
                    nextLevel.Add(currentLevel[i]);
                }
            }

            currentLevel = nextLevel;
        }

        MerkleRoot root = MerkleRoot.FromBytes(currentLevel[0]);
        return MerkleTreeIndex.Create(root, leaves);
    }
}
