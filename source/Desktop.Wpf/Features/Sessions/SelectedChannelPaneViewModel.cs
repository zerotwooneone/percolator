using Desktop.Wpf.Features.Sessions.Models;
using Desktop.Wpf.Features.Sessions.Queries;
using Desktop.Wpf.Features.Sessions.State;
using Desktop.Wpf.Shared.Mvvm;
using ObservableCollections;
using R3;

namespace Desktop.Wpf.Features.Sessions;

public enum SelectedPaneState
{
    None,
    Pending,
    Active,
    Offline,
    Failed
}

public sealed class SelectedChannelPaneViewModel : ViewModelBase
{
    public BindableReactiveProperty<SelectedPaneState> State { get; }
    public BindableReactiveProperty<string?> DisplayName { get; }
    public BindableReactiveProperty<string?> BannerText { get; }
    public BindableReactiveProperty<bool> IsInputEnabled { get; }

    public BindableReactiveProperty<object?> ActiveContent { get; }

    private readonly SelectedChannelModel _selection;
    private readonly PeerConnectionStateService _stateService;
    private readonly ISessionScopeFactory _sessionFactory;
    private readonly SelectedPeerConnectionStateCache _stateCache;

    private DisposableBag _bag;
    private DisposableBag _currentSelectionBag;
    private PeerConnectionModel? _currentConnection;

    public SelectedChannelPaneViewModel(
        SelectedChannelModel selection,
        PeerConnectionStateService stateService,
        ISessionScopeFactory sessionFactory,
        SelectedPeerConnectionStateCache stateCache)
    {
        _selection = selection;
        _stateService = stateService;
        _sessionFactory = sessionFactory;
        _stateCache = stateCache;

        State = new BindableReactiveProperty<SelectedPaneState>(SelectedPaneState.None).AddTo(ref _bag);
        DisplayName = new BindableReactiveProperty<string?>(null).AddTo(ref _bag);
        BannerText = new BindableReactiveProperty<string?>(null).AddTo(ref _bag);
        IsInputEnabled = new BindableReactiveProperty<bool>(false).AddTo(ref _bag);
        ActiveContent = new BindableReactiveProperty<object?>(null).AddTo(ref _bag);

        _selection.SelectedKey
            .DistinctUntilChanged()
            .Subscribe(key => OnSelectionChanged(key))
            .AddTo(ref _bag);

        // Initialize
        OnSelectionChanged(_selection.SelectedKey.Value);
    }

    private void OnSelectionChanged(PeerConnectionKey? selectedKey)
    {
        _currentSelectionBag.Dispose();
        _currentSelectionBag = default;
        _currentConnection = null;

        if (selectedKey is null)
        {
            State.Value = SelectedPaneState.None;
            DisplayName.Value = null;
            BannerText.Value = null;
            IsInputEnabled.Value = false;
            ActiveContent.Value = null;
            return;
        }

        var key = selectedKey.Value;
        if (key.Type != SecureChannelKeyType.SecureSession)
        {
            State.Value = SelectedPaneState.None;
            DisplayName.Value = null;
            BannerText.Value = null;
            IsInputEnabled.Value = false;
            ActiveContent.Value = null;
            return;
        }

        var model = _stateService.Connections.FirstOrDefault(c => c.ConnectionId == key.Value);

        if (model is null)
        {
            State.Value = SelectedPaneState.None;
            DisplayName.Value = null;
            BannerText.Value = null;
            IsInputEnabled.Value = false;
            ActiveContent.Value = null;
            return;
        }

        _currentConnection = model;
        DisplayName.Value = model.DisplayName.CurrentValue;

        var state = MapState(model);
        State.Value = state;

        switch (state)
        {
            case SelectedPaneState.Pending:
                BannerText.Value = "Establishing…";
                IsInputEnabled.Value = false;
                ActiveContent.Value = null;
                break;

            case SelectedPaneState.Failed:
                BannerText.Value = "Connection failed.";
                IsInputEnabled.Value = false;
                ActiveContent.Value = null;
                break;

            case SelectedPaneState.Offline:
                BannerText.Value = "Offline";
                IsInputEnabled.Value = false;
                ActiveContent.Value = ResolveChatContent(model, key);
                break;

            case SelectedPaneState.Active:
                BannerText.Value = "E2E Encryption Established";
                IsInputEnabled.Value = true;
                ActiveContent.Value = ResolveChatContent(model, key);
                break;

            default:
                BannerText.Value = null;
                IsInputEnabled.Value = false;
                ActiveContent.Value = null;
                break;
        }

        // Bind to model changes for reactive updates
        model.DisplayName
            .DistinctUntilChanged()
            .ObserveOnCurrentSynchronizationContext()
            .Subscribe(x => DisplayName.Value = x)
            .AddTo(ref _currentSelectionBag);

        model.Status
            .Select(MapStateFromStatus)
            .DistinctUntilChanged()
            .ObserveOnCurrentSynchronizationContext()
            .Subscribe(x => State.Value = x)
            .AddTo(ref _currentSelectionBag);
    }

    private object? ResolveChatContent(PeerConnectionModel model, PeerConnectionKey key)
    {
        var sessionId = key.Value.ToString("N");
        var header = new SessionHeader
        {
            DisplayName = model.DisplayName.CurrentValue,
            Initials = model.Initials.CurrentValue,
            IsOnline = true
        };

        var resolved = _sessionFactory.GetOrCreate(sessionId, header);

        var overlay = _stateCache.GetOrCreate(key);

        // Bridge draft text from per-channel overlay into per-session SessionContext draft.
        resolved.Context.Draft.Value = overlay.DraftMessageText.Value;

        resolved.Context.Draft
            .Subscribe(text =>
            {
                if (overlay.DraftMessageText.Value != text)
                    overlay.DraftMessageText.Value = text;
            })
            .AddTo(ref _currentSelectionBag);

        overlay.DraftMessageText
            .Subscribe(text =>
            {
                if (resolved.Context.Draft.Value != text)
                    resolved.Context.Draft.Value = text;
            })
            .AddTo(ref _currentSelectionBag);

        return resolved.ViewModel;
    }

    private static SelectedPaneState MapState(PeerConnectionModel model)
        => MapStateFromStatus(model.Status.CurrentValue);

    private static SelectedPaneState MapStateFromStatus(PeerConnectionStatus status)
        => status switch
        {
            PeerConnectionStatus.Direct => SelectedPaneState.Active,
            PeerConnectionStatus.Relay => SelectedPaneState.Active,
            PeerConnectionStatus.Group => SelectedPaneState.Active,
            _ => SelectedPaneState.Offline
        };

    protected override void DisposeCore()
    {
        _currentSelectionBag.Dispose();
        _bag.Dispose();
    }
}
