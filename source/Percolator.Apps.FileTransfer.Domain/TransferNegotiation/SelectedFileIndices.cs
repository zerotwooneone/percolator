using Percolator.Apps.FileTransfer.Domain.Common;
using Percolator.Apps.FileTransfer.Domain.Exceptions;

namespace Percolator.Apps.FileTransfer.Domain.TransferNegotiation;

public sealed record SelectedFileIndices
{
    private readonly HashSet<FileIndex> _indices;
    public IReadOnlySet<FileIndex> Indices => _indices;
    public int SelectedCount => _indices.Count;

    private SelectedFileIndices(HashSet<FileIndex> indices)
    {
        _indices = indices;
    }

    public bool Contains(FileIndex index) => _indices.Contains(index);

    public static SelectedFileIndices All(FileCount fileCount)
    {
        if (fileCount.Count <= 0)
            throw new DomainValidationException("FileCount must be greater than zero.");

        HashSet<FileIndex> set = new(fileCount.Count);
        for (int i = 0; i < fileCount.Count; i++)
        {
            set.Add(FileIndex.FromInt(i));
        }
        return new SelectedFileIndices(set);
    }

    public static SelectedFileIndices FromIndices(IEnumerable<FileIndex> indices, FileCount fileCount)
    {
        ArgumentNullException.ThrowIfNull(indices);
        HashSet<FileIndex> set = [];

        foreach (FileIndex index in indices)
        {
            if (index.Value < 0 || index.Value >= fileCount.Count)
            {
                throw new DomainValidationException(
                    $"Selected file index {index.Value} is out of bounds for bundle containing {fileCount.Count} files.");
            }
            set.Add(index);
        }

        if (set.Count == 0)
        {
            throw new DomainValidationException("Selected file set must contain at least one file entry.");
        }

        return new SelectedFileIndices(set);
    }

    public bool Equals(SelectedFileIndices? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        return _indices.SetEquals(other._indices);
    }

    public override int GetHashCode()
    {
        int hash = 0;
        foreach (FileIndex index in _indices)
        {
            hash ^= index.GetHashCode();
        }
        return HashCode.Combine(_indices.Count, hash);
    }
}
