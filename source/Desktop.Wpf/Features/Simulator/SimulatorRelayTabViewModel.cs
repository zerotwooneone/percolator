using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows;
using Microsoft.Extensions.Logging;
using Percolator.Application.Network;
using Percolator.Cryptography;
using R3;

namespace Desktop.Wpf.Features.Simulator;

public sealed class SimulatorRelayTabViewModel : IDisposable
{
    private readonly ISimulatorStateService _state;
    private readonly ISimulatedPeerDirectory _directory;
    private readonly PercolatorMessageService _messageService;
    private readonly ISimulatorRelayDeliveryService _delivery;
    private readonly ISimulatorDiagnosticsService _diagnostics;
    private readonly Percolator.Application.Identity.ActiveIdentityContext _active;
    private readonly ILogger<SimulatorRelayTabViewModel> _logger;
    private readonly ILoggerFactory _loggerFactory;

    private CancellationTokenSource? _autoDeliverCts;
    private Task? _autoDeliverLoop;

    private static readonly Guid MainNodeSentinelPeerId = new("88880000-0000-0000-0000-000000000000");

    private DisposableBag _bag;

    private readonly Dictionary<Guid, IDisposable> _relayCapableSubscriptions = new();

    private readonly ObservableCollection<SimulatedRelayQueuePanelViewModel> _relayPanels = new();
    public ReadOnlyObservableCollection<SimulatedRelayQueuePanelViewModel> RelayPanels { get; }

    public SimulatorRelayTabViewModel(
        ISimulatorStateService state,
        ISimulatedPeerDirectory directory,
        PercolatorMessageService messageService,
        ISimulatorRelayDeliveryService delivery,
        ISimulatorDiagnosticsService diagnostics,
        Percolator.Application.Identity.ActiveIdentityContext active,
        ILogger<SimulatorRelayTabViewModel> logger,
        ILoggerFactory loggerFactory)
    {
        _state = state;
        _directory = directory;
        _messageService = messageService;
        _delivery = delivery;
        _diagnostics = diagnostics;
        _active = active;
        _logger = logger;
        _loggerFactory = loggerFactory;

        RelayPanels = new ReadOnlyObservableCollection<SimulatedRelayQueuePanelViewModel>(_relayPanels);

        GlobalAutoRelayAll = new BindableReactiveProperty<bool>(false).AddTo(ref _bag);
        GlobalAutoRelayAll
            .Skip(1)
            .Subscribe(_ => ApplyGlobalAutoRelay())
            .AddTo(ref _bag);

        var refresh = Observable.Return(true).ToReactiveCommand<Unit>(_ => { });
        refresh.AsObservable().SubscribeAwait(async (_, ct) => await RefreshAsync(ct), AwaitOperation.Drop).AddTo(ref _bag);
        RefreshCommand = refresh.AddTo(ref _bag);

        _ = InitializeAsync();
    }

    public BindableReactiveProperty<bool> GlobalAutoRelayAll { get; }

    public ReactiveCommand<Unit> RefreshCommand { get; }

    private async Task InitializeAsync(CancellationToken ct = default)
    {
        await _directory.InitializeAsync(ct).ConfigureAwait(false);

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            ResetPanels();
            HookPeers();
        }
        else
        {
            await dispatcher.InvokeAsync(() =>
            {
                ResetPanels();
                HookPeers();
            });
        }

        await RefreshAsync(ct).ConfigureAwait(false);

        StartAutoDeliverLoop();
    }

    private void StartAutoDeliverLoop()
    {
        StopAutoDeliverLoop();

        _autoDeliverCts = new CancellationTokenSource();
        var token = _autoDeliverCts.Token;
        _autoDeliverLoop = Task.Run(async () => await AutoDeliverLoopAsync(token).ConfigureAwait(false), token);
    }

    private void StopAutoDeliverLoop()
    {
        try
        {
            _autoDeliverCts?.Cancel();
        }
        catch
        {
        }
        finally
        {
            _autoDeliverCts?.Dispose();
            _autoDeliverCts = null;
            _autoDeliverLoop = null;
        }
    }

    private async Task AutoDeliverLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Small delay to avoid tight looping.
                await Task.Delay(TimeSpan.FromMilliseconds(350), ct).ConfigureAwait(false);

                // Snapshot to avoid concurrent modification while iterating.
                var panels = _relayPanels.ToList();
                foreach (var panel in panels)
                {
                    ct.ThrowIfCancellationRequested();

                    // Global auto-relay forces per-panel on; otherwise respect panel toggle.
                    var enabled = GlobalAutoRelayAll.Value || panel.AutoDeliver.Value;
                    if (!enabled)
                    {
                        continue;
                    }

                    // Deliver a single item per cycle per relay to keep things fair.
                    await panel.DeliverNextAsync(ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[simulator] Auto-deliver loop error");
            }
        }
    }

    private void HookPeers()
    {
        var notify = (INotifyCollectionChanged)_directory.Peers;
        notify.CollectionChanged -= OnPeersChanged;
        notify.CollectionChanged += OnPeersChanged;

        WireRelayCapabilitySubscriptions();
    }

    private void WireRelayCapabilitySubscriptions()
    {
        foreach (var d in _relayCapableSubscriptions.Values)
        {
            d.Dispose();
        }
        _relayCapableSubscriptions.Clear();

        foreach (var peer in _directory.Peers)
        {
            var sub = peer.IsRelayCapable
                .DistinctUntilChanged()
                .Subscribe(_ => OnRelayCapabilityChanged());

            _relayCapableSubscriptions[peer.PeerId] = sub;
        }
    }

    private void OnRelayCapabilityChanged()
    {
        if (!Application.Current.Dispatcher.CheckAccess())
        {
            _ = Application.Current.Dispatcher.InvokeAsync(OnRelayCapabilityChanged);
            return;
        }

        ResetPanels();
        _ = RefreshAsync(CancellationToken.None);
    }

    private void OnPeersChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (!Application.Current.Dispatcher.CheckAccess())
        {
            _ = Application.Current.Dispatcher.InvokeAsync(() => OnPeersChanged(sender, e));
            return;
        }

        WireRelayCapabilitySubscriptions();
        ResetPanels();
        _ = RefreshAsync(CancellationToken.None);
    }

    private void ResetPanels()
    {
        foreach (var p in _relayPanels)
        {
            p.Dispose();
        }
        _relayPanels.Clear();

        foreach (var relay in _directory.Peers.Where(p => p.IsRelayCapable.CurrentValue))
        {
            _relayPanels.Add(CreatePanel(relay.PeerId, PeerNameById(relay.PeerId)));
        }

        ApplyGlobalAutoRelay();
    }

    private SimulatedRelayQueuePanelViewModel CreatePanel(Guid relayHostPeerId, string relayHostName)
    {
        return new SimulatedRelayQueuePanelViewModel(
            relayHostPeerId: relayHostPeerId,
            relayHostName: relayHostName,
            peerNameById: PeerNameById,
            getMainIdentityPkh: GetMainIdentityPkh,
            getRelayHostToMainSessionId: () => GetRelayHostToMainSessionIdAsync(relayHostPeerId),
            state: _state,
            delivery: _delivery,
            diagnostics: _diagnostics,
            logger: _loggerFactory.CreateLogger<SimulatedRelayQueuePanelViewModel>());
    }

    private byte[]? GetMainIdentityPkh()
    {
        try
        {
            var spki = _active.Keys?.IdentitySigningKey.ExportSubjectPublicKeyInfo();
            if (spki is null || spki.Length == 0) return null;
            return System.Security.Cryptography.SHA256.HashData(spki);
        }
        catch
        {
            return null;
        }
    }

    private void ApplyGlobalAutoRelay()
    {
        var enabled = GlobalAutoRelayAll.Value;
        foreach (var p in _relayPanels)
        {
            p.AutoDeliver.Value = enabled;
        }
    }

    public async Task RefreshAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        foreach (var p in _relayPanels)
        {
            ct.ThrowIfCancellationRequested();
            await p.RefreshAsync(ct).ConfigureAwait(false);
        }

        // Auto-deliver (nice-to-have for later: background loop).
        // For now: if toggled on, user still advances delivery manually via Next/All.
    }

    private async Task<SessionId?> GetRelayHostToMainSessionIdAsync(Guid relayHostPeerId)
    {
        var store = await _state.TryGetRuntimeStoreAsync(relayHostPeerId).ConfigureAwait(false);
        if (store is null) return null;

        var match = store.Sessions.FirstOrDefault(s => s.RemotePeerId == MainNodeSentinelPeerId);
        if (match is null) return null;
        if (match.SessionId == Guid.Empty) return null;

        return new SessionId(match.SessionId);
    }

    public void Dispose()
    {
        StopAutoDeliverLoop();

        foreach (var p in _relayPanels)
        {
            p.Dispose();
        }
        _relayPanels.Clear();

        try
        {
            var notify = (INotifyCollectionChanged)_directory.Peers;
            notify.CollectionChanged -= OnPeersChanged;
        }
        catch
        {
        }

        foreach (var d in _relayCapableSubscriptions.Values)
        {
            d.Dispose();
        }
        _relayCapableSubscriptions.Clear();

        _bag.Dispose();
        GlobalAutoRelayAll.Dispose();
    }

    private string PeerNameById(Guid peerId)
    {
        var model = _directory.Peers.FirstOrDefault(p => p.PeerId == peerId);
        if (model is not null)
        {
            var name = model.DisplayName.CurrentValue;
            if (!string.IsNullOrWhiteSpace(name)) return name;
        }

        var snap = _state.TryGetPeerSnapshot(peerId);
        if (snap is not null && !string.IsNullOrWhiteSpace(snap.DisplayName)) return snap.DisplayName!;
        return peerId.ToString()[..8];
    }
}
