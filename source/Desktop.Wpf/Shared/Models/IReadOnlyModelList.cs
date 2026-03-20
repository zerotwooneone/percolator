using R3;

namespace Desktop.Wpf.Shared.Models;

public interface IReadOnlyModelList<T> : IEnumerable<T>
{
    int Count { get; }

    T this[int index] { get; }

    IReadOnlyList<T> GetSnapshot();

    Observable<StoreListChange<T>> Changes { get; }
}