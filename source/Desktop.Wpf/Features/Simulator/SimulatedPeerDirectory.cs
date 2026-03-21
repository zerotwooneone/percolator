using System.Collections.ObjectModel;
using System.Windows;
using Desktop.Wpf.Shared.Models;
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

    private IDisposable? _changesSub;

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

                foreach (var model in _state.Peers.GetSnapshot())
                {
                    _peers.Add(model);
                    _byId[model.PeerId] = model;
                }
            }).ConfigureAwait(false);

            HookChanges();
        }
        finally
        {
            lock (_initGate)
            {
                _initializeTask = null;
            }
        }
    }

    private void HookChanges()
    {
        _changesSub?.Dispose();
        _changesSub = _state.Peers.Changes
            .Subscribe(change =>
            {
                _ = InvokeOnUiAsync(() => ApplyChange(change));
            });
    }

    private void ApplyChange(StoreListChange<SimulatedPeerModel> change)
    {
        if (change.Kind is StoreListChangeKind.Reset)
        {
            _peers.Clear();
            _byId.Clear();
            foreach (var m in change.Items)
            {
                _peers.Add(m);
                _byId[m.PeerId] = m;
            }
            return;
        }

        if (change.Kind is StoreListChangeKind.Add)
        {
            foreach (var m in change.Items)
            {
                _peers.Add(m);
                _byId[m.PeerId] = m;
            }
            return;
        }

        if (change.Kind is StoreListChangeKind.Remove)
        {
            foreach (var m in change.Items)
            {
                if (_byId.Remove(m.PeerId))
                {
                    _ = _peers.Remove(m);
                }
            }
            return;
        }

        if (change.Kind is StoreListChangeKind.Replace)
        {
            // Models should be stable; replace should be rare. Best-effort.
            foreach (var m in change.Items)
            {
                if (_byId.TryGetValue(m.PeerId, out var existing))
                {
                    var idx = _peers.IndexOf(existing);
                    if (idx >= 0) _peers[idx] = m;
                    _byId[m.PeerId] = m;
                }
                else
                {
                    _peers.Add(m);
                    _byId[m.PeerId] = m;
                }
            }
        }
    }

    public async Task<SimulatedPeerModel> AddPeerAsync(string? displayName, CancellationToken ct = default)
    {
        var peerId = await _state.AddPeerAsync(displayName, ct).ConfigureAwait(false);
        var model = _state.Peers.GetSnapshot().FirstOrDefault(x => x.PeerId == peerId);
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
        _changesSub?.Dispose();
        _changesSub = null;

        _peers.Clear();
        _byId.Clear();
    }
}
