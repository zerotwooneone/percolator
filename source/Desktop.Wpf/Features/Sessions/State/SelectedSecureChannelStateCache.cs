using Desktop.Wpf.Features.Sessions.Models;

namespace Desktop.Wpf.Features.Sessions.State;

public sealed class SelectedSecureChannelStateCache : IDisposable
{
    private readonly int _capacity;
    private readonly Dictionary<SecureChannelKey, SelectedSecureChannelStateModel> _items = new();
    private readonly LinkedList<SecureChannelKey> _lru = new();

    public SelectedSecureChannelStateCache(int capacity = 10)
    {
        _capacity = capacity;
    }

    public SelectedSecureChannelStateModel GetOrCreate(SecureChannelKey key)
    {
        if (_items.TryGetValue(key, out var existing))
        {
            Touch(key);
            return existing;
        }

        var model = new SelectedSecureChannelStateModel(key);
        _items[key] = model;
        _lru.AddFirst(key);
        EvictIfNeeded();
        return model;
    }

    private void Touch(SecureChannelKey key)
    {
        var node = _lru.Find(key);
        if (node is null) return;
        _lru.Remove(node);
        _lru.AddFirst(node);
    }

    private void EvictIfNeeded()
    {
        while (_items.Count > _capacity && _lru.Last is not null)
        {
            var key = _lru.Last.Value;
            _lru.RemoveLast();
            if (_items.Remove(key, out var evicted))
            {
                try { evicted.Dispose(); } catch { }
            }
        }
    }

    public void Dispose()
    {
        foreach (var it in _items.Values)
        {
            try { it.Dispose(); } catch { }
        }
        _items.Clear();
        _lru.Clear();
    }
}
