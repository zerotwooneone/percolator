using Desktop.Wpf.Features.Sessions.Models;
using Desktop.Wpf.Features.Self;
using Desktop.Wpf.Features.Sessions.State;
using Desktop.Wpf.Shared.Mvvm;
using ObservableCollections;
using R3;

namespace Desktop.Wpf.Features.Sessions;

public sealed class SessionsSidebarViewModel : ViewModelBase
{
    public BindableReactiveProperty<string> SearchText { get; }
    public System.ComponentModel.ICollectionView Items { get; }
    public BindableReactiveProperty<string?> SelectedSessionId { get; }
    public SelfIdentityModel Self { get; }
    public BindableReactiveProperty<bool> IsLoading { get; }
    public PendingHandshakesMenuViewModel PendingMenu { get; }

    private readonly ISynchronizedView<PeerConnectionModel, PeerConnectionListItemViewModel> _connectionsView;
    private readonly PeerConnectionStateService _stateService;
    private readonly SelectedChannelModel _selection;
    private readonly DisposableBag _bag;

    public SessionsSidebarViewModel(
        SelfIdentityModel self,
        PendingHandshakesMenuViewModel pendingMenu,
        PeerConnectionStateService stateService,
        SelectedChannelModel selection,
        IUiDispatcher ui)
    {
        Self = self;
        _stateService = stateService;
        _selection = selection;
        SearchText = new BindableReactiveProperty<string>("");
        SelectedSessionId = new BindableReactiveProperty<string?>(null);
        IsLoading = new BindableReactiveProperty<bool>(false);
        PendingMenu = pendingMenu;
        _bag = new DisposableBag();

        // 1. Create the view and track it
        _connectionsView = _stateService.Connections
            .CreateView(model => new PeerConnectionListItemViewModel(model))
            .AddTo(ref _bag);

        // 2. Dispose child VMs when removed from the domain
        _connectionsView.ObserveRemove().Subscribe(evt => evt.Value.View.Dispose()).AddTo(ref _bag);

        // 3. Define the dynamic filter logic
        bool FilterItem(PeerConnectionModel model, PeerConnectionListItemViewModel vm)
        {
            var term = SearchText.Value?.Trim() ?? "";
            if (string.IsNullOrEmpty(term)) return true;
            return vm.DisplayName.CurrentValue?.Contains(term, StringComparison.OrdinalIgnoreCase) == true;
        }
        
        // Attach it initially
        _connectionsView.AttachFilter(FilterItem);

        // 4. Create the synchronized list and wrap it in a WPF CollectionView for sorting
        var synchronizedList = _connectionsView.ToNotifyCollectionChanged(ui.CollectionEventDispatcher);
        var collectionView = (System.Windows.Data.ListCollectionView)System.Windows.Data.CollectionViewSource.GetDefaultView(synchronizedList);
        collectionView.CustomSort = new PeerConnectionChronologicalComparer();
        Items = collectionView;

        // 5. Force the Cysharp view to re-evaluate filters when search text changes
        SearchText
            .Subscribe(_ => _connectionsView.AttachFilter(FilterItem))
            .AddTo(ref _bag);

        // 6. Force WPF to re-sort and Cysharp to re-filter when existing domain items are mutated
        _stateService.StateMutated
            .ObserveOnCurrentSynchronizationContext()
            .Subscribe(_ => 
            {
                _connectionsView.AttachFilter(FilterItem);
                collectionView.Refresh();
            })
            .AddTo(ref _bag);

        // Selection is shared state. Sidebar selection writes through to SelectedChannelModel.
        SelectedSessionId
            .DistinctUntilChanged()
            .Subscribe(id =>
            {
                _selection.SelectedKey.Value = TryParseKey(id);
            }).AddTo(ref _bag);

        // And shared selection updates the ListBox selection.
        _selection.SelectedKey
            .DistinctUntilChanged()
            .Subscribe(key =>
            {
                var next = key is null ? null : key.Value.ToString();
                if (SelectedSessionId.Value != next)
                    SelectedSessionId.Value = next;
            }).AddTo(ref _bag);
    }

    private static PeerConnectionKey? TryParseKey(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;

        // Canonical format: "{Type}:{guid}"
        var parts = id.Split(':');
        if (parts.Length != 2) return null;

        if (!Enum.TryParse<SecureChannelKeyType>(parts[0], true, out var keyType)) return null;
        if (!Guid.TryParse(parts[1], out var guid)) return null;

        return keyType switch
        {
            SecureChannelKeyType.SecureSession => PeerConnectionKey.FromSessionId(guid),
            SecureChannelKeyType.PendingCorrelation => PeerConnectionKey.FromPendingCorrelationId(guid),
            SecureChannelKeyType.PendingSession => PeerConnectionKey.FromPendingSessionId(guid),
            _ => null
        };
    }

    protected override void DisposeCore()
    {
        _bag.Dispose();
        Disposable.Dispose(SearchText, SelectedSessionId);
    }
}

public sealed class PeerConnectionChronologicalComparer : System.Collections.IComparer
{
    public int Compare(object? x, object? y)
    {
        if (x is null && y is null) return 0;
        if (x is null) return 1;
        if (y is null) return -1;
        
        var vmX = (PeerConnectionListItemViewModel)x;
        var vmY = (PeerConnectionListItemViewModel)y;
        
        // Descending: newest at the top
        return vmY.LastUpdate.CurrentValue.CompareTo(vmX.LastUpdate.CurrentValue);
    }
}
