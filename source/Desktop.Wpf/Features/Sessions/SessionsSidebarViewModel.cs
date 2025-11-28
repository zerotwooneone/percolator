using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using R3;
using Desktop.Wpf.Shared.Navigation;
using Microsoft.Extensions.DependencyInjection;
using Desktop.Wpf.Features.Chat;
using System.Collections.Generic;

namespace Desktop.Wpf.Features.Sessions;

public sealed class SessionsSidebarViewModel : Features.Shell.ViewModelBase
{
    public BindableReactiveProperty<string> SearchText { get; }
    public ReadOnlyObservableCollection<SessionListItem> Items { get; }
    public BindableReactiveProperty<string?> SelectedSessionId { get; }

    private readonly ObservableCollection<SessionListItem> _items = new();

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly Dictionary<string, IServiceScope> _scopes = new();
    private readonly LinkedList<string> _lru = new();
    private const int ScopeCapacity = 3;

    public SessionsSidebarViewModel(ISessionDirectory directory, INavigationService navigation, IServiceProvider provider)
    {
        _scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();
        SearchText = new BindableReactiveProperty<string>("");
        SelectedSessionId = new BindableReactiveProperty<string?>(null);

        var filtered = SearchText
            .Select(text => text?.Trim() ?? "")
            .DistinctUntilChanged()
            .SelectAwait(async (text, ct) => await FilterAsync(directory, text, ct))
            .ObserveOnCurrentSynchronizationContext();

        filtered.Subscribe(list =>
        {
            _items.Clear();
            foreach (var i in list) _items.Add(i);
        });

        // Navigate to chat on selection using a per-session scope
        SelectedSessionId
            .Where(id => !string.IsNullOrEmpty(id))
            .Subscribe(id =>
            {
                if (id is null) return;
                if (!_scopes.TryGetValue(id, out var scope))
                {
                    scope = _scopeFactory.CreateScope();
                    _scopes[id] = scope;
                    _lru.AddFirst(id);
                    if (_lru.Count > ScopeCapacity)
                    {
                        var toEvict = _lru.Last!.Value;
                        _lru.RemoveLast();
                        if (_scopes.Remove(toEvict, out var evicted))
                        {
                            try { evicted.Dispose(); } catch { }
                        }
                    }
                }
                else
                {
                    // Move to front (most recently used)
                    var node = _lru.Find(id);
                    if (node is not null)
                    {
                        _lru.Remove(node);
                        _lru.AddFirst(node);
                    }
                }

                var ctx = scope.ServiceProvider.GetRequiredService<Desktop.Wpf.Features.Sessions.SessionContext>();
                // Populate minimal context from our current list (fallback to directory if needed)
                var entry = _items.FirstOrDefault(x => x.Id == id);
                ctx.SetSessionId(id);
                if (entry is not null)
                {
                    ctx.PeerName.Value = entry.DisplayName.Value;
                    ctx.Initials.Value = entry.Initials.Value;
                    ctx.IsOnline.Value = entry.IsOnline.Value;
                }

                var chatView = scope.ServiceProvider.GetRequiredService<ChatView>();
                if (chatView.DataContext is ChatViewModel cvm)
                {
                    cvm.SetSession(id);
                }
                navigation.Navigate(chatView);
            });

        // Navigate back to welcome when selection cleared
        SelectedSessionId
            .Where(id => string.IsNullOrEmpty(id))
            .Subscribe(_ => navigation.Navigate(null));

        Items = new ReadOnlyObservableCollection<SessionListItem>(_items);
    }

    private static async ValueTask<SessionListItem[]> FilterAsync(ISessionDirectory dir, string text, CancellationToken ct)
    {
        var data = await dir.GetAllAsync(ct);
        if (string.IsNullOrWhiteSpace(text)) return data.ToArray();
        text = text.ToLowerInvariant();
        return data.Where(x => x.DisplayName.Value.ToLowerInvariant().Contains(text) || (x.LastMessagePreview.Value ?? "").ToLowerInvariant().Contains(text)).ToArray();
    }

    protected override void DisposeCore()
    {
        Disposable.Dispose(SearchText, SelectedSessionId);
    }
}
