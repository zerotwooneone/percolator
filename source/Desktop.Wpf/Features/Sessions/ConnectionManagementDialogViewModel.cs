using Desktop.Wpf.Features.Sessions.Commands;
using Desktop.Wpf.Features.Self;
using Desktop.Wpf.Features.Sessions.Models;
using Desktop.Wpf.Shared.Mvvm;
using ObservableCollections;
using Percolator.Application.Identity;
using Percolator.Application.Network;
using Percolator.Cryptography;
using R3;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Desktop.Wpf.Features.Sessions.Queries;
using MediatR;
using Percolator.Network;

namespace Desktop.Wpf.Features.Sessions;


public sealed class RouteModeOption
{
    public RouteModeOption(string key, string displayName)
    {
        Key = key;
        DisplayName = displayName;
    }

    public string Key { get; }
    public string DisplayName { get; }
}

public sealed class RelayHostOption
{
    public RelayHostOption(PeerId peerId, string displayName)
    {
        PeerId = peerId;
        DisplayName = displayName;
    }

    public PeerId PeerId { get; }
    public string DisplayName { get; }
}

public sealed class ConnectionManagementDialogViewModel : ViewModelBase
{
    private readonly DisposableBag _bag;

    private readonly ActiveIdentityContext _active;
    private readonly PeerConnectionStateService _stateService;
    private readonly IIdentityStateService _identityStateService;
    private readonly IMediator _mediator;

    private readonly object _relayHostRefreshLock = new();
    private bool _relayHostRefreshQueued;

    private readonly ISynchronizedView<PeerConnectionModel, PeerConnectionModel> _connectionsView;
    private readonly NotifyCollectionChangedSynchronizedViewList<PeerConnectionModel> _connectionsNotify;
    private readonly ISynchronizedView<PeerPendingInvitationModel, PendingInvitationItemViewModel> _pendingInvitationsView;
    private readonly ObservableCollection<RouteModeOption> _routeModeOptions = new();
    private readonly ObservableList<RelayHostOption> _relayHostOptions = new();
    private readonly ISynchronizedView<RelayHostOption, RelayHostOption> _relayHostView;

    public BindableReactiveProperty<int> SelectedTabIndex { get; }

    public BindableReactiveProperty<string?> TargetDisplayNameText { get; }

    public ReadOnlyObservableCollection<RouteModeOption> RouteModeOptions { get; }
    public BindableReactiveProperty<RouteModeOption?> SelectedRouteMode { get; }

    public BindableReactiveProperty<string?> DirectEndpointText { get; }

    public BindableReactiveProperty<string?> TargetPkhText { get; }

    public NotifyCollectionChangedSynchronizedViewList<RelayHostOption> RelayHostOptions { get; }
    public BindableReactiveProperty<RelayHostOption?> SelectedRelayHost { get; }

    public BindableReactiveProperty<string?> PhaseText { get; }
    public BindableReactiveProperty<string?> ErrorText { get; }

    public BindableReactiveProperty<string?> InviteTokenText { get; }

    public INotifyCollectionChangedSynchronizedViewList<PendingInvitationItemViewModel> PendingInvitations { get; }

    public ReadOnlyReactiveProperty<string> IdentityDisplayName => _identityStateService.DisplayName;

    public AsyncRelayCommand AcceptInvitationCommand { get; }
    public AsyncRelayCommand BurnInvitationCommand { get; }

    public AsyncRelayCommand SearchAndConnectCommand { get; }

    public AsyncRelayCommand DecodeAndInitiateCommand { get; }

    public ConnectionManagementDialogViewModel(
        ActiveIdentityContext active,
        PeerConnectionStateService stateService,
        IIdentityStateService identityStateService,
        IUiDispatcher ui,
        IMediator mediator)
    {
        _active = active;
        _stateService = stateService;
        _identityStateService = identityStateService;
        _mediator = mediator;

        // Create synchronized view of connections for relay host refresh
        _connectionsView = _stateService.Connections
            .CreateView(model => model)
            .AddTo(ref _bag);

        _connectionsNotify = _connectionsView.ToNotifyCollectionChanged(ui.CollectionEventDispatcher);
        ((INotifyCollectionChanged)_connectionsNotify).CollectionChanged += OnConnectionsChanged;

        // Create synchronized view for relay host options
        _relayHostView = _relayHostOptions.CreateView(x => x).AddTo(ref _bag);
        RelayHostOptions = _relayHostView.ToNotifyCollectionChanged(ui.CollectionEventDispatcher);

        // Create synchronized view for pending invitations from state service
        _pendingInvitationsView = _stateService.PendingInbound
            .CreateView(model => new PendingInvitationItemViewModel(model, ui))
            .AddTo(ref _bag);
        _pendingInvitationsView.ObserveRemove().Subscribe(evt => evt.Value.View.Dispose()).AddTo(ref _bag);
        PendingInvitations = _pendingInvitationsView.ToNotifyCollectionChanged(ui.CollectionEventDispatcher);

        SelectedTabIndex = new BindableReactiveProperty<int>(0).AddTo(ref _bag);

        TargetDisplayNameText = new BindableReactiveProperty<string?>(null).AddTo(ref _bag);

        RouteModeOptions = new ReadOnlyObservableCollection<RouteModeOption>(_routeModeOptions);
        SelectedRouteMode = new BindableReactiveProperty<RouteModeOption?>(null).AddTo(ref _bag);

        DirectEndpointText = new BindableReactiveProperty<string?>(null).AddTo(ref _bag);

        TargetPkhText = new BindableReactiveProperty<string?>(null).AddTo(ref _bag);

        SelectedRelayHost = new BindableReactiveProperty<RelayHostOption?>(null).AddTo(ref _bag);

        PhaseText = new BindableReactiveProperty<string?>(null).AddTo(ref _bag);
        ErrorText = new BindableReactiveProperty<string?>(null).AddTo(ref _bag);

        InviteTokenText = new BindableReactiveProperty<string?>(null).AddTo(ref _bag);

        AcceptInvitationCommand = new AsyncRelayCommand(async obj => await ExecuteAcceptAsync(obj));
        BurnInvitationCommand = new AsyncRelayCommand(async obj => await ExecuteBurnAsync(obj));

        SearchAndConnectCommand = new AsyncRelayCommand(async _ => await ExecuteNetworkSearchAsync());

        DecodeAndInitiateCommand = new AsyncRelayCommand(async _ => await ExecuteImportTokenAsync());

        _ = InitializeAsync().ContinueWith(static t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
    }

    private void OnConnectionsChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => QueueRelayHostRefresh();

    private void QueueRelayHostRefresh()
    {
        lock (_relayHostRefreshLock)
        {
            if (_relayHostRefreshQueued) return;
            _relayHostRefreshQueued = true;
        }

        _ = RefreshRelayHostOptionsAsync(CancellationToken.None).ContinueWith(t =>
        {
            lock (_relayHostRefreshLock)
            {
                _relayHostRefreshQueued = false;
            }
        }, TaskContinuationOptions.None);
    }

    private async Task ExecuteImportTokenAsync(CancellationToken ct = default)
    {
        ResetStatus();
        PhaseText.Value = "Processing...";

        var result = await _mediator.Send(new DecodeAndQueueInviteCommand(InviteTokenText.Value), ct);

        if (result is DecodeAndQueueInviteResult.Failed failed)
        {
            ErrorText.Value = failed.ErrorMessage;
            PhaseText.Value = null;
            return;
        }

        PhaseText.Value = null;
        SelectedTabIndex.Value = 0;
    }

    private async Task InitializeAsync(CancellationToken ct = default)
    {
        InitializeRouteModeOptions();
        await RefreshRelayHostOptionsAsync(ct);

        var desired = ((ICollection<PendingInvitationItemViewModel>)PendingInvitations).Count > 0 ? 0 : 1;
        SelectedTabIndex.Value = desired;
    }

    private void InitializeRouteModeOptions()
    {
        _routeModeOptions.Clear();
        _routeModeOptions.Add(new RouteModeOption("direct", "Direct"));
        _routeModeOptions.Add(new RouteModeOption("relay", "Via Relay Host"));
        SelectedRouteMode.Value ??= _routeModeOptions.FirstOrDefault();
    }

    private async Task RefreshRelayHostOptionsAsync(CancellationToken ct)
    {
        if (_active.Identity is null)
        {
            _relayHostOptions.Clear();
            return;
        }

        GetRelayHostOptionsResult result;
        try
        {
            result = await _mediator.Send(new GetRelayHostOptionsQuery(_active.Identity.SelfIdentityId.Value), ct).ConfigureAwait(false);
        }
        catch
        {
            _relayHostOptions.Clear();
            return;
        }

        _relayHostOptions.Clear();
        foreach (var o in result.Options)
            _relayHostOptions.Add(new RelayHostOption(o.PeerId, o.DisplayName));
        SelectedRelayHost.Value ??= _relayHostOptions.FirstOrDefault();
    }


    private async Task ExecuteAcceptAsync(object? obj)
    {
        if (obj is not PendingInvitationItemViewModel item) return;
        if (_active.Identity is null) return;

        ApprovePendingSessionResult result;
        try
        {
            result = await _mediator.Send(new ApprovePendingSessionCommand(item.PendingSessionId, _active.Identity.SelfIdentityId));
        }
        catch (Exception ex)
        {
            item.StatusText.Value = $"Failed: {ex.Message}";
            return;
        }

        switch (result)
        {
            case ApprovePendingSessionResult.Accepted accepted:
                item.StatusText.Value = "Accepted";
                item.SendPath = accepted.SendPath;
                item.RequestCorrelationId = accepted.RequestCorrelationId.Value.ToString();
                item.IsExpired.Value = false;
                break;
            case ApprovePendingSessionResult.RejectedNotReady:
                item.StatusText.Value = "Rejected: Not Ready";
                break;
            case ApprovePendingSessionResult.RejectedInvalid:
                item.StatusText.Value = "Rejected: Invalid";
                break;
            case ApprovePendingSessionResult.RejectedExpired:
                item.StatusText.Value = "Rejected: Expired";
                item.IsExpired.Value = true;
                break;
            case ApprovePendingSessionResult.Failed failed:
                item.StatusText.Value = $"Failed: {failed.ErrorMessage}";
                break;
            default:
                item.StatusText.Value = "Failed: Unknown";
                break;
        }
    }

    private async Task ExecuteBurnAsync(object? obj)
    {
        if (obj is not PendingInvitationItemViewModel item) return;
        if (_active.Identity is null) return;

        try
        {
            await _mediator.Send(new RejectPendingSessionCommand(item.PendingSessionId));
        }
        catch
        {
            return;
        }
    }

    private async Task ExecuteNetworkSearchAsync(CancellationToken ct = default)
    {
        ResetStatus();
        PhaseText.Value = "Starting...";

        if (_active.Identity is null)
        {
            ErrorText.Value = "Identity not loaded.";
            PhaseText.Value = null;
            return;
        }

        PhaseText.Value = "Validating...";

        var routeMode = SelectedRouteMode.Value;
        if (routeMode is null)
        {
            ErrorText.Value = "Select a route mode.";
            PhaseText.Value = null;
            return;
        }

        // Relay host options are derived from persisted direct sessions. Refresh on demand so the dropdown
        // reflects newly-established sessions even if no store-level collection change is emitted.
        await RefreshRelayHostOptionsAsync(ct);

        var result = await _mediator.Send(new ConnectViaNetworkCommand(
            routeMode.Key,
            DirectEndpointText.Value,
            TargetPkhText.Value,
            SelectedRelayHost.Value?.PeerId,
            TargetDisplayNameText.Value), ct);

        switch (result)
        {
            case ConnectViaNetworkResult.Success:
                PhaseText.Value = "Handshake initiated.";
                break;
            case ConnectViaNetworkResult.TargetOffline:
                ErrorText.Value = "Target offline.";
                PhaseText.Value = null;
                break;
            case ConnectViaNetworkResult.Failed failed:
                ErrorText.Value = failed.ErrorMessage;
                PhaseText.Value = null;
                break;
        }
    }

    private void ResetStatus()
    {
        PhaseText.Value = null;
        ErrorText.Value = null;
    }


    protected override void DisposeCore()
    {
        Disposable.Dispose(SelectedTabIndex);
        Disposable.Dispose(TargetDisplayNameText);
        Disposable.Dispose(SelectedRouteMode);
        Disposable.Dispose(DirectEndpointText);
        Disposable.Dispose(TargetPkhText);
        Disposable.Dispose(SelectedRelayHost);
        Disposable.Dispose(PhaseText);
        Disposable.Dispose(ErrorText);
        Disposable.Dispose(InviteTokenText);
        ((INotifyCollectionChanged)_connectionsNotify).CollectionChanged -= OnConnectionsChanged;
        _connectionsNotify.Dispose();
        _bag.Dispose();
    }
}
