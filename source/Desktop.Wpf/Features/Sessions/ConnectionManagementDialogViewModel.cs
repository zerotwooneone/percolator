using Desktop.Wpf.Features.Sessions.Commands;
using Desktop.Wpf.Features.Sessions.Models;
using Desktop.Wpf.Features.Sessions.Queries;
using Desktop.Wpf.Shared.Mvvm;
using ObservableCollections;
using Percolator.Application.Identity;
using Percolator.Application.Network;
using Percolator.Cryptography;
using R3;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using MediatR;
using Percolator.Network;

namespace Desktop.Wpf.Features.Sessions;


public sealed class TransportRouteOption
{
    public TransportRouteOption(Guid? relayHostPeerId, string displayName)
    {
        RelayHostPeerId = relayHostPeerId;
        DisplayName = displayName;
    }

    // null => Direct P2P (local mesh)
    public Guid? RelayHostPeerId { get; }
    public string DisplayName { get; }
}

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

    private readonly IMainInvitationInbox _inbox;
    private readonly IMainInvitationInboxEvents _inboxEvents;
    private readonly ActiveIdentityContext _active;
    private readonly PeerConnectionStateService _stateService;
    private readonly IMediator _mediator;

    private readonly object _relayHostRefreshLock = new();
    private bool _relayHostRefreshQueued;

    private readonly ISynchronizedView<PeerConnectionModel, PeerConnectionModel> _connectionsView;
    private readonly NotifyCollectionChangedSynchronizedViewList<PeerConnectionModel> _connectionsNotify;

    private readonly ObservableList<PendingInvitationItemViewModel> _pendingInvitations = new();
    private readonly ISynchronizedView<PendingInvitationItemViewModel, PendingInvitationItemViewModel> _pendingInvitationsView;
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

    public AsyncRelayCommand AcceptInvitationCommand { get; }
    public AsyncRelayCommand BurnInvitationCommand { get; }
    public AsyncRelayCommand RefreshInboxCommand { get; }

    public AsyncRelayCommand SearchAndConnectCommand { get; }

    public AsyncRelayCommand DecodeAndInitiateCommand { get; }

    public ConnectionManagementDialogViewModel(
        IMainInvitationInbox inbox,
        IMainInvitationInboxEvents inboxEvents,
        ActiveIdentityContext active,
        PeerConnectionStateService stateService,
        IUiDispatcher ui,
        IMediator mediator)
    {
        _inbox = inbox;
        _inboxEvents = inboxEvents;
        _active = active;
        _stateService = stateService;
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

        // Create synchronized view for pending invitations
        _pendingInvitationsView = _pendingInvitations.CreateView(x => x).AddTo(ref _bag);
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

        RefreshInboxCommand = new AsyncRelayCommand(async _ => await RefreshInboxAsync());
        AcceptInvitationCommand = new AsyncRelayCommand(async obj => await ExecuteAcceptAsync(obj));
        BurnInvitationCommand = new AsyncRelayCommand(async obj => await ExecuteBurnAsync(obj));

        SearchAndConnectCommand = new AsyncRelayCommand(async _ => await ExecuteNetworkSearchAsync());

        DecodeAndInitiateCommand = new AsyncRelayCommand(async _ => await ExecuteImportTokenAsync());

        _inboxEvents.Changed
            .SubscribeAwait(async (_, ct) => await RefreshInboxAsync(ct).ConfigureAwait(false), AwaitOperation.Drop)
            .AddTo(ref _bag);

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
        await RefreshInboxAsync(ct);
        SelectedTabIndex.Value = 0;
    }

    private async Task InitializeAsync(CancellationToken ct = default)
    {
        InitializeRouteModeOptions();
        await RefreshRelayHostOptionsAsync(ct);

        await RefreshInboxAsync(ct);

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

    public async Task RefreshInboxAsync(CancellationToken ct = default)
    {
        IReadOnlyList<PendingInvitationDto> open;
        try
        {
            open = await _inbox.GetOpenAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            return;
        }

        var items = open
            .OrderByDescending(x => x.CreatedAtUtc)
            .Select(p => new PendingInvitationItemViewModel(
                new PendingSessionId(p.PendingSessionId),
                p.PeerName,
                ComputeInitials(p.PeerName),
                p.IsRelayed,
                p.IsRelayed
                    ? $"Via relay: {p.RelayPeerName}{(string.IsNullOrWhiteSpace(p.RelayEndpoint) ? "" : $" ({p.RelayEndpoint})")}"
                    : null))
            .ToList();

        foreach (var it in _pendingInvitations)
            it.Dispose();
        _pendingInvitations.Clear();
        foreach (var it in items)
            _pendingInvitations.Add(it);
    }

    private async Task ExecuteAcceptAsync(object? obj)
    {
        if (obj is not PendingInvitationItemViewModel item) return;

        ApprovePendingSessionResult result;
        try
        {
            result = await _mediator.Send(new ApprovePendingSessionCommand(item.PendingSessionId));
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
                await RefreshInboxAsync();
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
                await RefreshInboxAsync();
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

        try
        {
            await _mediator.Send(new RejectPendingSessionCommand(item.PendingSessionId));
        }
        catch
        {
            return;
        }

        await RefreshInboxAsync();
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

    private static string ComputeInitials(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "?";
        var parts = name.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 1)
            return parts[0].Substring(0, Math.Min(2, parts[0].Length)).ToUpperInvariant();
        return (parts[0][0].ToString() + parts[^1][0].ToString()).ToUpperInvariant();
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
