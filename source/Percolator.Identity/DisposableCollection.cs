using System.Collections;

namespace Percolator.Identity;

public class DisposableCollection<T> : IDisposable, IEnumerable<T> where T : IDisposable
{
    private readonly IEnumerable<T> _collection;

    public DisposableCollection(IEnumerable<T> collection)
    {
        _collection = collection ?? throw new ArgumentNullException(nameof(collection));
    }

    public void Dispose()
    {
        foreach (var item in _collection)
        {
            item.Dispose();
        }
        GC.SuppressFinalize(this);
    }

    public IEnumerator<T> GetEnumerator()
    {
        return _collection.GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }
}
