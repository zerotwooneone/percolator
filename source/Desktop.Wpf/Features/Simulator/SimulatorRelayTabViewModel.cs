using Desktop.Wpf.Shared.Mvvm;
using Microsoft.Extensions.Logging;
using ObservableCollections;
using Percolator.Application.Network;
using Percolator.Cryptography;
using R3;

namespace Desktop.Wpf.Features.Simulator;

public sealed class SimulatorRelayTabViewModel : IDisposable
{
    private readonly IUiDispatcher _ui;

    private readonly ISimulatorStateService _state;
    private readonly ISimulatorInitializer _directory;
    private readonly PercolatorMessageService _messageService;
    private readonly ISimulatorRelayDeliveryService _delivery;
    private readonly ISimulatorDiagnosticsService _diagnostics;
    private readonly Percolator.Application.Identity.ActiveIdentityContext _active;
    private readonly ILogger<SimulatorRelayTabViewModel> _logger;
    private readonly ILoggerFactory _loggerFactory;

    private CancellationTokenSource? _autoDeliverCts;
    private Task? _autoDeliverLoop;

    private static readonly Guid MainNodeSentinelPeerId = new("88880000-0000-0000-0000-000000000000");

    private ISynchronizedView<Desktop.Wpf.Features.Simulator.Models.SimulatedRelayModel, SimulatedRelayQueuePanelViewModel>? _relayPanels;
    private NotifyCollectionChangedSynchronizedViewList<SimulatedRelayQueuePanelViewModel>? _relayPanelsNotify;

    private DisposableBag _bag;

    public SimulatorRelayTabViewModel(
        ISimulatorStateService state,
        ISimulatorInitializer directory,
        PercolatorMessageService messageService,
        ISimulatorRelayDeliveryService delivery,
        ISimulatorDiagnosticsService diagnostics,
        Percolator.Application.Identity.ActiveIdentityContext active,
        IUiDispatcher ui,
        ILogger<SimulatorRelayTabViewModel> logger,
        ILoggerFactory loggerFactory)
    {
        _ui = ui;
        _state = state;
        _directory = directory;
        _messageService = messageService;
        _delivery = delivery;
        _diagnostics = diagnostics;
        _active = active;
        _logger = logger;
        _loggerFactory = loggerFactory;

        GlobalAutoRelayAll = new BindableReactiveProperty<bool>(false).AddTo(ref _bag);
        GlobalAutoRelayAll
            .Skip(1)
            .Subscribe(_ => ApplyGlobalAutoRelay())
            .AddTo(ref _bag);

        RefreshCommand = new ReactiveCommand<Unit>().AddTo(ref _bag);
        RefreshCommand
            .AsObservable()
            .SubscribeAwait(async (_, ct) => await RefreshAsync(ct), AwaitOperation.Drop)
            .AddTo(ref _bag);
    }

    public BindableReactiveProperty<bool> GlobalAutoRelayAll { get; }

    public ReactiveCommand<Unit> RefreshCommand { get; }

    public NotifyCollectionChangedSynchronizedViewList<SimulatedRelayQueuePanelViewModel> RelayPanels
        => _relayPanelsNotify ?? throw new InvalidOperationException("ViewModel not initialized.");

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await _state.InitializeAsync(ct).ConfigureAwait(false);
        await _directory.InitializeAsync(ct).ConfigureAwait(false);

        InitializePanelsView();
        ApplyGlobalAutoRelay();

        await RefreshAsync(ct).ConfigureAwait(false);

        StartAutoDeliverLoop();
    }

    private void InitializePanelsView()
    {
        _relayPanelsNotify?.Dispose();
        _relayPanelsNotify = null;
        _relayPanels?.Dispose();
        _relayPanels = null;

        _relayPanels = _state.Relays
            .CreateView(CreatePanel)
            .AddTo(ref _bag);

        _relayPanels.ObserveRemove()
            .Subscribe(evt => evt.Value.View.Dispose())
            .AddTo(ref _bag);

        _relayPanelsNotify = _relayPanels.ToNotifyCollectionChanged(_ui.CollectionEventDispatcher);
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
                var panels = await _ui.InvokeAsync(() =>
                    {
                        var snapshot = new List<SimulatedRelayQueuePanelViewModel>();
                        foreach (var panel in RelayPanels)
                        {
                            snapshot.Add(panel);
                        }
                        return snapshot;
                    })
                    .ConfigureAwait(false);
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

    private SimulatedRelayQueuePanelViewModel CreatePanel(Desktop.Wpf.Features.Simulator.Models.SimulatedRelayModel relay)
    {
        var relayHostPeerId = relay.RelayHostPeerId;
        return new SimulatedRelayQueuePanelViewModel(
            relayHostPeerId: relay.RelayHostPeerId,
            relayHostName: PeerNameById(relayHostPeerId),
            peerNameById: PeerNameById,
            getRelayHostToMainSessionId: () => GetRelayHostToMainSessionIdAsync(relayHostPeerId),
            ui: _ui,
            state: _state,
            delivery: _delivery,
            diagnostics: _diagnostics,
            logger: _loggerFactory.CreateLogger<SimulatedRelayQueuePanelViewModel>());
    }

    private void ApplyGlobalAutoRelay()
    {
        var enabled = GlobalAutoRelayAll.Value;
        if (_relayPanelsNotify is null) return;

        foreach (var p in _relayPanelsNotify)
        {
            p.AutoDeliver.Value = enabled;
        }
    }

    public async Task RefreshAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        await Task.CompletedTask.ConfigureAwait(false);
    }

    private async Task<SessionId?> GetRelayHostToMainSessionIdAsync(Guid relayHostPeerId)
    {
        await Task.CompletedTask.ConfigureAwait(false);

        var peer = _state.Peers.FirstOrDefault(p => p.PeerId == relayHostPeerId);
        if (peer is null) return null;

        var match = peer.Sessions
            .Select(kv => kv.Value)
            .FirstOrDefault(s => s.RemotePeerId.Value == MainNodeSentinelPeerId);

        return match?.Id;
    }

    public void Dispose()
    {
        StopAutoDeliverLoop();

        _relayPanelsNotify?.Dispose();
        _relayPanelsNotify = null;
        _relayPanels?.Dispose();
        _relayPanels = null;

        _bag.Dispose();
    }

    private string PeerNameById(Guid peerId)
    {
        var model = _state.Peers.FirstOrDefault(p => p.PeerId == peerId);
        if (model is not null)
        {
            var name = model.DisplayName.CurrentValue;
            if (!string.IsNullOrWhiteSpace(name)) return name;
        }
        return peerId.ToString()[..8];
    }
}
