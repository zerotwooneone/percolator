using System.Collections;
using Percolator.Apps.FileTransfer.Domain.Common;
using Percolator.Apps.FileTransfer.Domain.Exceptions;

namespace Percolator.Apps.FileTransfer.Domain.TransferNegotiation;

public sealed record SelectedFileIndices
{
    public bool IsAll { get; }
    private readonly int _totalFileCount;
    private readonly HashSet<FileIndex>? _indices;

    private IReadOnlySet<FileIndex>? _cachedAllSet;

    public IReadOnlySet<FileIndex> Indices => IsAll
        ? (_cachedAllSet ??= new AllFileIndicesReadOnlySet(_totalFileCount))
        : _indices!;

    public int SelectedCount => IsAll ? _totalFileCount : _indices!.Count;

    private SelectedFileIndices(int totalFileCount)
    {
        IsAll = true;
        _totalFileCount = totalFileCount;
        _indices = null;
    }

    private SelectedFileIndices(HashSet<FileIndex> indices)
    {
        IsAll = false;
        _totalFileCount = indices.Count;
        _indices = indices;
    }

    public bool Contains(FileIndex index) => IsAll
        ? (index.Value >= 0 && index.Value < _totalFileCount)
        : _indices!.Contains(index);

    public static SelectedFileIndices All(FileCount fileCount)
    {
        if (fileCount.Count <= 0)
            throw new DomainValidationException("FileCount must be greater than zero.");

        return new SelectedFileIndices(fileCount.Count);
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

        if (set.Count == fileCount.Count)
        {
            return new SelectedFileIndices(fileCount.Count);
        }

        return new SelectedFileIndices(set);
    }

    public bool Equals(SelectedFileIndices? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        if (IsAll != other.IsAll) return false;
        if (IsAll) return _totalFileCount == other._totalFileCount;
        return _indices!.SetEquals(other._indices!);
    }

    public override int GetHashCode()
    {
        if (IsAll) return HashCode.Combine(true, _totalFileCount);
        int hash = 0;
        foreach (FileIndex index in _indices!)
        {
            hash ^= index.GetHashCode();
        }
        return HashCode.Combine(false, _indices!.Count, hash);
    }

    private sealed class AllFileIndicesReadOnlySet : IReadOnlySet<FileIndex>
    {
        private readonly int _total;

        public AllFileIndicesReadOnlySet(int total) => _total = total;

        public int Count => _total;

        public bool Contains(FileIndex item) => item.Value >= 0 && item.Value < _total;

        public bool IsProperSubsetOf(IEnumerable<FileIndex> other) => IsSubsetOf(other) && other.Any(x => !Contains(x));

        public bool IsProperSupersetOf(IEnumerable<FileIndex> other) => IsSupersetOf(other) && other.Distinct().Count() < _total;

        public bool IsSubsetOf(IEnumerable<FileIndex> other) => other.Distinct().Count() >= _total && other.All(Contains);

        public bool IsSupersetOf(IEnumerable<FileIndex> other) => other.All(Contains);

        public bool Overlaps(IEnumerable<FileIndex> other) => other.Any(Contains);

        public bool SetEquals(IEnumerable<FileIndex> other)
        {
            if (other is AllFileIndicesReadOnlySet all) return all._total == _total;
            if (other is IReadOnlyCollection<FileIndex> coll && coll.Count != _total) return false;
            HashSet<FileIndex> set = other is HashSet<FileIndex> s ? s : [.. other];
            return set.Count == _total && set.All(Contains);
        }

        public IEnumerator<FileIndex> GetEnumerator() => Enumerable.Range(0, _total).Select(FileIndex.FromInt).GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
