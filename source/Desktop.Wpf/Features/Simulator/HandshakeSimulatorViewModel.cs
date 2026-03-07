using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using R3;

namespace Desktop.Wpf.Features.Simulator;

public sealed class HandshakeSimulatorViewModel : IDisposable
{
    private DisposableBag _bag;

    public HandshakeSimulatorViewModel(
        SimulatorPeersTabViewModel peers,
        SimulatorHandshakesTabViewModel handshakes,
        SimulatorRelayTabViewModel relay,
        SimulatorSessionsTabViewModel sessions,
        SimulatorDiagnosticsTabViewModel diagnostics)
    {
        Peers = peers;
        Handshakes = handshakes;
        Relay = relay;
        Sessions = sessions;
        Diagnostics = diagnostics;

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
        ct.ThrowIfCancellationRequested();
        return SetStatusOnUiAsync("Export diagnostics not implemented yet");
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
