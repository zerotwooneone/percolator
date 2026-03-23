using System.Collections.ObjectModel;
using System.Windows;
using ObservableCollections;
using R3;

namespace Desktop.Wpf.Features.Simulator;

public interface ISimulatedPeerDirectory : IDisposable
{
    ReadOnlyObservableCollection<SimulatedPeerModel> Peers { get; }

    Task InitializeAsync(CancellationToken ct = default);

    Task<SimulatedPeerModel> AddPeerAsync(string? displayName, CancellationToken ct = default);
    Task RemovePeerAsync(Guid peerId, CancellationToken ct = default);
}

public sealed class SimulatedPeerDirectory : ISimulatedPeerDirectory
{
    private readonly ISimulatorStateService _state;

    private readonly object _initGate = new();
    private Task? _initializeTask;

    private readonly ObservableCollection<SimulatedPeerModel> _peers = new();
    public ReadOnlyObservableCollection<SimulatedPeerModel> Peers { get; }

    private readonly Dictionary<Guid, SimulatedPeerModel> _byId = new();

    private IDisposable? _peersSub;

    public SimulatedPeerDirectory(ISimulatorStateService state)
    {
        _state = state;
        Peers = new ReadOnlyObservableCollection<SimulatedPeerModel>(_peers);
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        Task? inFlight;
        lock (_initGate)
        {
            inFlight = _initializeTask;
            if (inFlight is null || inFlight.IsCompleted)
            {
                _initializeTask = InitializeCoreAsync(ct);
                inFlight = _initializeTask;
            }
        }

        await inFlight.ConfigureAwait(false);
    }

    private async Task InitializeCoreAsync(CancellationToken ct)
    {
        try
        {
            await _state.InitializeAsync(ct).ConfigureAwait(false);

            // WPF collection + models must be mutated on UI thread.
            await InvokeOnUiAsync(() =>
            {
                _peers.Clear();
                _byId.Clear();

                foreach (var model in _state.Peers)
                {
                    _peers.Add(model);
                    _byId[model.PeerId] = model;
                }
            }).ConfigureAwait(false);

            HookPeers();
        }
        finally
        {
            lock (_initGate)
            {
                _initializeTask = null;
            }
        }
    }

    private void HookPeers()
    {
        _peersSub?.Dispose();

        var peers = _state.Peers;
        _peersSub = Observable.Merge(
                peers.ObserveAdd().Select(static _ => Unit.Default),
                peers.ObserveRemove().Select(static _ => Unit.Default),
                peers.ObserveReplace().Select(static _ => Unit.Default),
                peers.ObserveReset().Select(static _ => Unit.Default))
            .SubscribeAwait(async (_,__) =>  await InvokeOnUiAsync(RebuildFromState));
    }

    private void RebuildFromState()
    {
        _peers.Clear();
        _byId.Clear();
        foreach (var m in _state.Peers)
        {
            _peers.Add(m);
            _byId[m.PeerId] = m;
        }
    }

    public async Task<SimulatedPeerModel> AddPeerAsync(string? displayName, CancellationToken ct = default)
    {
        var peerId = await _state.AddPeerAsync(displayName, ct).ConfigureAwait(false);
        var model = _state.Peers.FirstOrDefault(x => x.PeerId == peerId);
        if (model is null) throw new InvalidOperationException("Peer was created but no model was published.");
        return model;
    }

    public async Task RemovePeerAsync(Guid peerId, CancellationToken ct = default)
    {
        await _state.RemovePeerAsync(peerId, ct).ConfigureAwait(false);
    }

    private static Task InvokeOnUiAsync(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }

        return dispatcher.InvokeAsync(action).Task;
    }

    public void Dispose()
    {
        _peersSub?.Dispose();
        _peersSub = null;

        _peers.Clear();
        _byId.Clear();
    }
}
