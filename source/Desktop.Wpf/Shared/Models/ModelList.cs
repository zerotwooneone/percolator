using System.Collections;
using R3;

namespace Desktop.Wpf.Shared.Models;

public sealed class ModelList<T> : IReadOnlyModelList<T>, IDisposable
{
    private readonly object _gate = new();
    private readonly List<T> _items = new();
    private readonly Subject<StoreListChange<T>> _changes = new();
    private bool _disposed;

    public int Count
    {
        get
        {
            lock (_gate) return _items.Count;
        }
    }

    public T this[int index]
    {
        get
        {
            lock (_gate) return _items[index];
        }
    }

    public Observable<StoreListChange<T>> Changes => _changes;

    public IReadOnlyList<T> GetSnapshot()
    {
        lock (_gate) return _items.ToArray();
    }

    public void Reset(IEnumerable<T> items)
    {
        if (items is null) throw new ArgumentNullException(nameof(items));

        StoreListChange<T> change;
        lock (_gate)
        {
            ThrowIfDisposed();
            _items.Clear();
            _items.AddRange(items);
            change = new StoreListChange<T>(StoreListChangeKind.Reset, _items.ToArray());
        }

        _changes.OnNext(change);
    }

    public void Add(T item)
    {
        StoreListChange<T> change;
        int index;
        lock (_gate)
        {
            ThrowIfDisposed();
            index = _items.Count;
            _items.Add(item);
            change = new StoreListChange<T>(StoreListChangeKind.Add, new[] { item }, index);
        }

        _changes.OnNext(change);
    }

    public bool Remove(T item)
    {
        StoreListChange<T> change;
        int index;

        lock (_gate)
        {
            ThrowIfDisposed();
            index = _items.IndexOf(item);
            if (index < 0) return false;
            _items.RemoveAt(index);
            change = new StoreListChange<T>(StoreListChangeKind.Remove, new[] { item }, index);
        }

        _changes.OnNext(change);
        return true;
    }

    public bool ReplaceAt(int index, T item)
    {
        StoreListChange<T> change;

        lock (_gate)
        {
            ThrowIfDisposed();
            if (index < 0 || index >= _items.Count) return false;
            _items[index] = item;
            change = new StoreListChange<T>(StoreListChangeKind.Replace, new[] { item }, index);
        }

        _changes.OnNext(change);
        return true;
    }

    public void Clear() => Reset(Array.Empty<T>());

    public IEnumerator<T> GetEnumerator() => GetSnapshot().GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }

        // R3 subjects complete observers on dispose by default.
        _changes.Dispose();
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(ModelList<T>));
    }
}
