using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using R3;
using Desktop.Wpf.Shared.Navigation;
using Microsoft.Extensions.DependencyInjection;
using Desktop.Wpf.Features.Chat;

namespace Desktop.Wpf.Features.Sessions;

public sealed class SessionsSidebarViewModel : Features.Shell.ViewModelBase
{
    public BindableReactiveProperty<string> SearchText { get; }
    public ReadOnlyObservableCollection<SessionListItem> Items { get; }
    public BindableReactiveProperty<string?> SelectedSessionId { get; }

    private readonly ObservableCollection<SessionListItem> _items = new();

    public SessionsSidebarViewModel(ISessionDirectory directory, INavigationService navigation, IServiceProvider provider)
    {
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

        // Navigate to chat on selection
        SelectedSessionId
            .Where(id => !string.IsNullOrEmpty(id))
            .Subscribe(id =>
            {
                var chatView = provider.GetRequiredService<ChatView>();
                if (chatView.DataContext is ChatViewModel cvm && id is not null)
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
        return data.Where(x => x.DisplayName.ToLowerInvariant().Contains(text) || (x.LastMessagePreview ?? "").ToLowerInvariant().Contains(text)).ToArray();
    }

    protected override void DisposeCore()
    {
        Disposable.Dispose(SearchText, SelectedSessionId);
    }
}
