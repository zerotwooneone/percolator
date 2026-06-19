using Desktop.Wpf.Features.Chat;
using Desktop.Wpf.Features.Sessions.Models;
using Desktop.Wpf.Features.Sessions.Queries;
using Desktop.Wpf.Features.Sessions.State;
using Desktop.Wpf.Shared.Mvvm;
using Percolator.Network;
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

    public BindableReactiveProperty<ChatViewModel?> ActiveContent { get; }

    private readonly SelectedChannelModel _selection;
    private readonly PeerConnectionStateService _stateService;
    private readonly ISessionScopeFactory _sessionFactory;
    private readonly IChatReloadCoordinator _reloadCoordinator;

    private DisposableBag _bag;

    public SelectedChannelPaneViewModel(
        SelectedChannelModel selection,
        PeerConnectionStateService stateService,
        ISessionScopeFactory sessionFactory,
        IChatReloadCoordinator reloadCoordinator)
    {
        _selection = selection;
        _stateService = stateService;
        _sessionFactory = sessionFactory;
        _reloadCoordinator = reloadCoordinator;

        // Create observable stream of the selected connection model
        var selectedConnectionObservable = _selection.SelectedKey
            .DistinctUntilChanged()
            .Select(key =>
            {
                if (key is null) return null;
                return _stateService.Connections.FirstOrDefault(c => c.Key == key.Value);
            })
            .DistinctUntilChanged();

        // DisplayName: project from selected connection's DisplayName, null if no selection
        DisplayName = selectedConnectionObservable
            .Select(model => model is null ? Observable.Return<string?>(null) : model.DisplayName)
            .Switch()
            .DistinctUntilChanged()
            .ObserveOnCurrentSynchronizationContext()
            .ToBindableReactiveProperty(null)
            .AddTo(ref _bag);

        var stateObservable = selectedConnectionObservable
            .Select(model => model is null
                ? Observable.Return(SelectedPaneState.None)
                : model.Status.Select(MapStateFromStatus))
            .Switch()
            .DistinctUntilChanged();

        State = stateObservable
            .DistinctUntilChanged()
            .ObserveOnCurrentSynchronizationContext()
            .ToBindableReactiveProperty(SelectedPaneState.None)
            .AddTo(ref _bag);

        BannerText = stateObservable
            .Select(state =>
            {
                return state switch
                {
                    SelectedPaneState.Pending => "Establishing…",
                    SelectedPaneState.Failed => "Connection failed.",
                    SelectedPaneState.Offline => "Offline",
                    SelectedPaneState.Active => "E2E Encryption Established",
                    _ => null
                };
            })
            .DistinctUntilChanged()
            .ObserveOnCurrentSynchronizationContext()
            .ToBindableReactiveProperty(null)
            .AddTo(ref _bag);

        IsInputEnabled = stateObservable
            .Select(state => SelectedPaneState.Active == state)
            .DistinctUntilChanged()
            .ObserveOnCurrentSynchronizationContext()
            .ToBindableReactiveProperty(false)
            .AddTo(ref _bag);

        // ActiveContent: project ChatViewModel from selection
        ActiveContent = _selection.SelectedKey
            .DistinctUntilChanged()
            .Select(key =>
            {
                if (key is null || key.Value.Type != SecureChannelKeyType.SecureSession)
                    return null;

                var keyVal = key.Value;
                var model = _stateService.Connections.FirstOrDefault(c => c.Key == keyVal);
                if (model is null)
                    return null;

                return ResolveChatContentWithSubscriptions(model, keyVal);
            })
            .ObserveOnCurrentSynchronizationContext()
            .ToBindableReactiveProperty(null)
            .AddTo(ref _bag);
    }

    private ChatViewModel? ResolveChatContentWithSubscriptions(PeerConnectionModel model, PeerConnectionKey key)
    {
        var sessionId = new DirectSessionId(key.Value);
        var header = new SessionHeader
        {
            DisplayName = model.DisplayName.CurrentValue,
            Initials = model.Initials.CurrentValue
        };

        var resolved = _sessionFactory.GetOrCreate(sessionId, header);

        // Trigger initial load from SQLite on background thread
        _reloadCoordinator.TriggerReloadForSession(sessionId);

        return resolved.ViewModel;
    }

    private static SelectedPaneState MapStateFromStatus(PeerConnectionStatus status)
        => status switch
        {
            PeerConnectionStatus.Direct => SelectedPaneState.Active,
            PeerConnectionStatus.Relay => SelectedPaneState.Active,
            PeerConnectionStatus.Group => SelectedPaneState.Active,
            PeerConnectionStatus.PendingOutbound => SelectedPaneState.Pending,
            _ => SelectedPaneState.Offline
        };

    protected override void DisposeCore()
    {
        _bag.Dispose();
    }
}
