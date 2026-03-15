using System.Collections.ObjectModel;
using System.Collections.Specialized;
using R3;
using Desktop.Wpf.Shared.Navigation;
using Desktop.Wpf.Features.Self;
using Desktop.Wpf.Shared.Mvvm;
using Desktop.Wpf.Features.Sessions.State;
using Desktop.Wpf.Features.Sessions.Models;

namespace Desktop.Wpf.Features.Sessions;

public sealed class SessionsSidebarViewModel : ViewModelBase
{
    public BindableReactiveProperty<string> SearchText { get; }
    public ReadOnlyObservableCollection<SecureChannelListItemViewModel> Items { get; }
    public BindableReactiveProperty<string?> SelectedSessionId { get; }
    public SelfIdentityModel Self { get; }
    public BindableReactiveProperty<bool> IsLoading { get; }
    public PendingHandshakesMenuViewModel PendingMenu { get; }

    private readonly ObservableCollection<SecureChannelListItemViewModel> _items = new();

    private readonly ISecureChannelsStore _store;
    private readonly SelectedChannelModel _selection;
    private ISessionConductor? _conductor;

    public SessionsSidebarViewModel(INavigationService navigation,
        SelfIdentityModel self,
                                   PendingHandshakesMenuViewModel pendingMenu,
        ISecureChannelsStore store,
        SelectedChannelModel selection)
    {
        Self = self;
        _store = store;
        _selection = selection;
        SearchText = new BindableReactiveProperty<string>("");
        SelectedSessionId = new BindableReactiveProperty<string?>(null);
        IsLoading = new BindableReactiveProperty<bool>(false);
        PendingMenu = pendingMenu;

        RebuildFromStore();
        ((INotifyCollectionChanged)_store.Channels).CollectionChanged += OnChannelsChanged;

        var filtered = SearchText
            .Select(text => text?.Trim() ?? "")
            .DistinctUntilChanged()
            .Select(text => ApplyFilter(text))
            .ObserveOnCurrentSynchronizationContext();

        filtered.Subscribe(list =>
        {
            _items.Clear();
            foreach (var i in list) _items.Add(i);
        });

        // Selection is shared state. Sidebar selection writes through to SelectedChannelModel.
        SelectedSessionId
            .DistinctUntilChanged()
            .Subscribe(id =>
            {
                _selection.SelectedKey.Value = TryParseKey(id);
            });

        // And shared selection updates the ListBox selection.
        _selection.SelectedKey
            .DistinctUntilChanged()
            .Subscribe(key =>
            {
                var next = key is null ? null : key.Value.Value.ToString("N");
                if (SelectedSessionId.Value != next)
                    SelectedSessionId.Value = next;
            });

        Items = new ReadOnlyObservableCollection<SecureChannelListItemViewModel>(_items);
    }

    private static SecureChannelKey? TryParseKey(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        if (!Guid.TryParse(id, out var guid)) return null;
        return SecureChannelKey.FromSessionId(guid);
    }

    private SecureChannelListItemViewModel[] ApplyFilter(string text)
    {
        var snapshot = _items.ToArray();
        if (string.IsNullOrWhiteSpace(text)) return snapshot;
        text = text.ToLowerInvariant();
        return snapshot.Where(x => x.DisplayName.Value.ToLowerInvariant().Contains(text) || (x.LastSnippet.Value ?? "").ToLowerInvariant().Contains(text)).ToArray();
    }

    private void RebuildFromStore()
    {
        var list = _store.Channels
            .Select(m => new SecureChannelListItemViewModel(m))
            .ToArray();

        _items.Clear();
        foreach (var it in list)
        {
            _items.Add(it);
        }
    }

    private void OnChannelsChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => RebuildFromStore();

    protected override void DisposeCore()
    {
        ((INotifyCollectionChanged)_store.Channels).CollectionChanged -= OnChannelsChanged;
        Disposable.Dispose(SearchText, SelectedSessionId);
    }

    public void SetConductor(ISessionConductor conductor)
    {
        _conductor = conductor;
    }
}
