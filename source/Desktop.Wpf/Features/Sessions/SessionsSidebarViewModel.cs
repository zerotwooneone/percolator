using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using R3;
using Desktop.Wpf.Shared.Navigation;
using Desktop.Wpf.Features.Chat;
using System.Collections.Generic;
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

    private readonly ISessionScopeFactory _sessionFactory;
    private readonly ISecureChannelsStore _store;
    private ISessionConductor? _conductor;

    public SessionsSidebarViewModel(INavigationService navigation,
        SelfIdentityModel self,
                                   ISessionScopeFactory sessionFactory,
                                   PendingHandshakesMenuViewModel pendingMenu,
        ISecureChannelsStore store)
    {
        Self = self;
        _sessionFactory = sessionFactory;
        _store = store;
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

        // Navigate to chat on selection using a factory-managed per-session scope
        SelectedSessionId
            .Where(id => !string.IsNullOrEmpty(id))
            .Subscribe(id =>
            {
                if (id is null) return;
                var entry = _items.FirstOrDefault(x => x.Id == id);
                if (entry is null) return;

                // Pending/failed/group items do not have an active chat session yet.
                if (entry.BadgeType.Value is SecureChannelBadgeType.Pending
                    or SecureChannelBadgeType.Failed
                    or SecureChannelBadgeType.Group)
                {
                    return;
                }
                var header = entry is null ? null : new SessionHeader
                {
                    DisplayName = entry.DisplayName.Value,
                    Initials = entry.Initials.Value,
                    IsOnline = entry.IsOnline.Value
                };
                var resolved = _sessionFactory.GetOrCreate(id, header);
                if (_conductor is not null)
                    _conductor.Show(resolved.ViewModel);
                else
                    navigation.Navigate(resolved.ViewModel);
            });

        // Navigate back to welcome when selection cleared
        SelectedSessionId
            .Where(id => string.IsNullOrEmpty(id))
            .Subscribe(_ =>
            {
                if (_conductor is not null)
                    _conductor.Show(null);
                else
                    navigation.Navigate(null);
            });

        Items = new ReadOnlyObservableCollection<SecureChannelListItemViewModel>(_items);
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
