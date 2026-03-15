using System.Collections.ObjectModel;
using System.Windows;
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
                foreach (var existing in _peers.ToArray())
                {
                    existing.Dispose();
                }

                _peers.Clear();
                _byId.Clear();

                foreach (var dto in _state.Peers)
                {
                    var model = new SimulatedPeerModel(
                        dto.PeerId,
                        dto.DisplayName,
                        dto.IsOnline,
                        dto.Relay.IsRelayCapable,
                        dto.ReverseSignalKeys.IdentitySigningKeySpki,
                        dto.ReverseSignalKeys.IdentitySigningKeyPrivateKeyEcPrivateKey,
                        dto.RuntimeState);

                    _peers.Add(model);
                    _byId[model.PeerId] = model;

                    WirePersistence(model);
                }
            }).ConfigureAwait(false);
        }
        finally
        {
            lock (_initGate)
            {
                _initializeTask = null;
            }
        }
    }

    public async Task<SimulatedPeerModel> AddPeerAsync(string? displayName, CancellationToken ct = default)
    {
        var peerId = await _state.AddPeerAsync(displayName, ct);

        var dto = _state.Peers.FirstOrDefault(p => p.PeerId == peerId);
        var model = new SimulatedPeerModel(
            peerId,
            dto?.DisplayName ?? displayName,
            dto?.IsOnline ?? true,
            dto?.Relay.IsRelayCapable ?? false,
            dto?.ReverseSignalKeys.IdentitySigningKeySpki ?? Array.Empty<byte>(),
            dto?.ReverseSignalKeys.IdentitySigningKeyPrivateKeyEcPrivateKey ?? Array.Empty<byte>(),
            dto?.RuntimeState);

        await InvokeOnUiAsync(() =>
        {
            _peers.Add(model);
            _byId[peerId] = model;
            WirePersistence(model);
        }).ConfigureAwait(false);

        return model;
    }

    public async Task RemovePeerAsync(Guid peerId, CancellationToken ct = default)
    {
        await InvokeOnUiAsync(() =>
        {
            if (_byId.TryGetValue(peerId, out var model))
            {
                _byId.Remove(peerId);
                _peers.Remove(model);
                model.Dispose();
            }
        }).ConfigureAwait(false);

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

    private void WirePersistence(SimulatedPeerModel model)
    {
        var d1 = model.DisplayName
            .DistinctUntilChanged()
            .SubscribeAwait(async (name, ct) =>
                await _state.UpdateDisplayNameAsync(model.PeerId, name, ct),
                AwaitOperation.Drop);

        var d2 = model.IsOnline
            .DistinctUntilChanged()
            .SubscribeAwait(async (isOnline, ct) =>
                await _state.SetOnlineAsync(model.PeerId, isOnline, ct),
                AwaitOperation.Drop);

        var d3 = model.IsRelayCapable
            .DistinctUntilChanged()
            .SubscribeAwait(async (isRelayCapable, ct) =>
                await _state.SetRelayCapableAsync(model.PeerId, isRelayCapable, ct),
                AwaitOperation.Drop);

        var d4 = model.RuntimeState
            .DistinctUntilChanged()
            .SubscribeAwait(async (runtimeState, ct) =>
                    await _state.SetRuntimeStateAsync(model.PeerId, runtimeState, ct),
                AwaitOperation.Drop);

        model.Track(d1);
        model.Track(d2);
        model.Track(d3);
        model.Track(d4);
    }

    public void Dispose()
    {
        foreach (var p in _peers)
        {
            p.Dispose();
        }

        _peers.Clear();
        _byId.Clear();
    }
}
