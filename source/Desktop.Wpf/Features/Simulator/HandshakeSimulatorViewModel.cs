using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using R3;

namespace Desktop.Wpf.Features.Simulator;

public sealed class HandshakeSimulatorViewModel : IDisposable
{
    private DisposableBag _bag;

    private readonly ISimulatorDiagnosticsService _diagnostics;
    private readonly ISimulatorStateService _state;

    public HandshakeSimulatorViewModel(
        SimulatorPeersTabViewModel peers,
        SimulatorHandshakesTabViewModel handshakes,
        SimulatorRelayTabViewModel relay,
        SimulatorSessionsTabViewModel sessions,
        SimulatorDiagnosticsTabViewModel diagnostics,
        ISimulatorDiagnosticsService diagnosticsService,
        ISimulatorStateService state)
    {
        Peers = peers;
        Handshakes = handshakes;
        Relay = relay;
        Sessions = sessions;
        Diagnostics = diagnostics;

        _diagnostics = diagnosticsService;
        _state = state;

        SelectedTab = new BindableReactiveProperty<SimulatorTabKind>(SimulatorTabKind.Peers).AddTo(ref _bag);
        CurrentTabViewModel = SelectedTab
            .Select(kind => kind switch
            {
                SimulatorTabKind.Peers => (object)Peers,
                SimulatorTabKind.Handshakes => Handshakes,
                SimulatorTabKind.Relay => Relay,
                SimulatorTabKind.Sessions => Sessions,
                SimulatorTabKind.Diagnostics => Diagnostics,
                _ => (object)Peers
            })
            .ToBindableReactiveProperty<object>(Peers)
            .AddTo(ref _bag);

        Status = new BindableReactiveProperty<string?>(null).AddTo(ref _bag);

        var reset = Observable.Return(true).ToReactiveCommand<Unit>(_ => { });
        reset.AsObservable()
            .SubscribeAwait(async (_, ct) => await ExecuteResetAsync(ct), AwaitOperation.Drop)
            .AddTo(ref _bag);
        ResetSimulatorStateCommand = reset.AddTo(ref _bag);

        var export = Observable.Return(true).ToReactiveCommand<Unit>(_ => { });
        export.AsObservable()
            .SubscribeAwait(async (_, ct) => await ExecuteExportDiagnosticsAsync(ct), AwaitOperation.Drop)
            .AddTo(ref _bag);
        ExportDiagnosticsCommand = export.AddTo(ref _bag);

        SelectTabCommand = new ReactiveCommand<SimulatorTabKind>(tab => SelectedTab.Value = tab).AddTo(ref _bag);
    }

    public SimulatorPeersTabViewModel Peers { get; }
    public SimulatorHandshakesTabViewModel Handshakes { get; }
    public SimulatorRelayTabViewModel Relay { get; }
    public SimulatorSessionsTabViewModel Sessions { get; }
    public SimulatorDiagnosticsTabViewModel Diagnostics { get; }

    public BindableReactiveProperty<string?> Status { get; }

    public BindableReactiveProperty<SimulatorTabKind> SelectedTab { get; }

    public BindableReactiveProperty<object> CurrentTabViewModel { get; }

    public ReactiveCommand<Unit> ResetSimulatorStateCommand { get; }

    public ReactiveCommand<Unit> ExportDiagnosticsCommand { get; }

    public ReactiveCommand<SimulatorTabKind> SelectTabCommand { get; }

    private async Task ExecuteResetAsync(CancellationToken ct)
    {
        try
        {
            await Peers.ResetAsync(ct).ConfigureAwait(false);
            await SetStatusOnUiAsync("Simulator state reset").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await SetStatusOnUiAsync($"Error: {ex.Message}").ConfigureAwait(false);
        }
    }

    private Task ExecuteExportDiagnosticsAsync(CancellationToken ct)
    {
        return ExecuteExportDiagnosticsInnerAsync(ct);
    }

    private async Task ExecuteExportDiagnosticsInnerAsync(CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            var json = await BuildDiagnosticBundleJsonAsync(ct).ConfigureAwait(false);

            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null || dispatcher.CheckAccess())
            {
                Clipboard.SetText(json);
            }
            else
            {
                await dispatcher.InvokeAsync(() => Clipboard.SetText(json));
            }

            await SetStatusOnUiAsync("Diagnostic bundle copied to clipboard").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await SetStatusOnUiAsync($"Error: {ex.Message}").ConfigureAwait(false);
        }
    }

    private async Task<string> BuildDiagnosticBundleJsonAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var peers = _state.Peers
            .Select(p => new
            {
                p.PeerId,
                p.DisplayName,
                p.IsOnline,
                IsRelayCapable = p.Relay?.IsRelayCapable == true,
                ConnectionMode = p.Connection?.Mode.ToString(),
                RelayPeerId = p.Connection?.RelayPeerId
            })
            .ToList();

        var relayQueueSummary = _state.Peers
            .Where(p => p.Relay?.IsRelayCapable == true)
            .Select(p => new
            {
                RelayHostPeerId = p.PeerId,
                RelayHostName = p.DisplayName,
                OpaqueQueueCount = p.Relay?.OpaqueQueue?.Items?.Count ?? 0,
                PreKeyBundleCount = p.Relay?.PreKeyStore?.PublishedBundles?.Count ?? 0
            })
            .ToList();

        var recentEvents = _diagnostics
            .GetRecentEvents(500)
            .Select(e => new
            {
                e.TimestampUtc,
                e.EventType,
                e.Message,
                e.PeerId,
                e.RelayHostPeerId,
                e.AckId,
                e.ContextTag
            })
            .ToList();

        var sessionSummaries = new List<object>();
        foreach (var p in _state.Peers)
        {
            ct.ThrowIfCancellationRequested();
            var store = await _state.TryGetRuntimeStoreAsync(p.PeerId, ct).ConfigureAwait(false);
            if (store is null) continue;

            foreach (var s in store.Sessions)
            {
                sessionSummaries.Add(new
                {
                    LocalPeerId = p.PeerId,
                    RemotePeerId = s.RemotePeerId,
                    s.SessionId,
                    s.ProtocolVersion,
                    SendCounter = s.SendCounter,
                    RecvCounter = s.RecvCounter,
                    s.SkippedKeysCount,
                    s.CreatedAtUtc,
                    s.LastUsedAtUtc
                });
            }
        }

        var bundle = new
        {
            Version = 1,
            GeneratedAtUtc = DateTimeOffset.UtcNow,
            Peers = peers,
            RelayQueueSummary = relayQueueSummary,
            RecentEvents = recentEvents,
            SessionSummaries = sessionSummaries
        };

        return JsonSerializer.Serialize(bundle, new JsonSerializerOptions
        {
            WriteIndented = true
        });
    }

    private Task SetStatusOnUiAsync(string? status)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            Status.Value = status;
            return Task.CompletedTask;
        }

        return dispatcher.InvokeAsync(() => Status.Value = status).Task;
    }

    public void Dispose()
    {
        (Peers as IDisposable)?.Dispose();
        (Handshakes as IDisposable)?.Dispose();
        (Relay as IDisposable)?.Dispose();
        (Sessions as IDisposable)?.Dispose();
        (Diagnostics as IDisposable)?.Dispose();
        _bag.Dispose();
    }
}
