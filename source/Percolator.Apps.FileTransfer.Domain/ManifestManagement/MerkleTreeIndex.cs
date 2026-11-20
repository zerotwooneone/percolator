using Percolator.Apps.FileTransfer.Domain.Common;
using Percolator.Apps.FileTransfer.Domain.Exceptions;
using Percolator.Apps.FileTransfer.Domain.TransferNegotiation;

namespace Percolator.Apps.FileTransfer.Domain.ManifestManagement;

public sealed record MerkleTreeIndex
{
    public MerkleRoot Root { get; }
    public IReadOnlyList<ChunkHash> Leaves { get; }
    public int LeafCount => Leaves.Count;

    private MerkleTreeIndex(MerkleRoot root, IReadOnlyList<ChunkHash> leaves)
    {
        Root = root;
        Leaves = leaves;
    }

    public static MerkleTreeIndex Create(MerkleRoot root, IReadOnlyList<ChunkHash> leaves)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(leaves);
        if (leaves.Count == 0)
            throw new DomainValidationException("Merkle tree leaves cannot be empty.");

        return new MerkleTreeIndex(root, leaves.ToArray());
    }

    public ChunkHash GetLeaf(ChunkIndex index)
    {
        if (index.Value < 0 || index.Value >= Leaves.Count)
            throw new DomainValidationException($"Chunk index {index.Value} is out of bounds for leaf count {Leaves.Count}.");

        return Leaves[index.Value];
    }

    public bool VerifyLeaf(ChunkIndex index, ReadOnlySpan<byte> plaintextChunk)
    {
        if (index.Value < 0 || index.Value >= Leaves.Count) return false;

        ChunkHash computed = MerkleTreeBuilder.HashLeaf(plaintextChunk);
        return computed.Span.SequenceEqual(Leaves[index.Value].Span);
    }

    public bool Equals(MerkleTreeIndex? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        if (Root != other.Root || Leaves.Count != other.Leaves.Count) return false;
        for (int i = 0; i < Leaves.Count; i++)
        {
            if (Leaves[i] != other.Leaves[i]) return false;
        }
        return true;
    }

    public override int GetHashCode() => Root.GetHashCode();
}
