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
    public INotifyCollectionChangedSynchronizedViewList<PeerConnectionListItemViewModel> Items { get; }
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

        // 3. Attach the dynamic filter logic
        void AttachFilter()
        {
            _connectionsView.AttachFilter((model, vm) => 
            {
                var term = SearchText.Value?.Trim() ?? "";
                if (string.IsNullOrEmpty(term)) return true;
                return vm.DisplayName.CurrentValue?.Contains(term, StringComparison.OrdinalIgnoreCase) == true;
            });
        }

        AttachFilter();

        // 4. Force the view to re-evaluate when search text changes
        SearchText.Subscribe(_ => 
        {
            _connectionsView.ResetFilter();
            AttachFilter();
        }).AddTo(ref _bag);

        // 5. Expose directly to WPF via the UI Dispatcher
        Items = _connectionsView.ToNotifyCollectionChanged(ui.CollectionEventDispatcher);

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
                var next = key is null ? null : key.Value.Value.ToString("N");
                if (SelectedSessionId.Value != next)
                    SelectedSessionId.Value = next;
            }).AddTo(ref _bag);
    }

    private static PeerConnectionKey? TryParseKey(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        if (!Guid.TryParse(id, out var guid)) return null;
        return PeerConnectionKey.FromSessionId(guid);
    }

    protected override void DisposeCore()
    {
        _bag.Dispose();
        Disposable.Dispose(SearchText, SelectedSessionId);
    }
}
