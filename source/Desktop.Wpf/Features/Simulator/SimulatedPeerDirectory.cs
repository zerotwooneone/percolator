using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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
        await _state.InitializeAsync(ct);

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
                dto.ReverseSignalKeys.IdentitySigningKeyPrivateKeyEcPrivateKey);

            _peers.Add(model);
            _byId[model.PeerId] = model;

            WirePersistence(model);
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
            dto?.ReverseSignalKeys.IdentitySigningKeyPrivateKeyEcPrivateKey ?? Array.Empty<byte>());

        _peers.Add(model);
        _byId[peerId] = model;

        WirePersistence(model);

        return model;
    }

    public async Task RemovePeerAsync(Guid peerId, CancellationToken ct = default)
    {
        if (_byId.TryGetValue(peerId, out var model))
        {
            _byId.Remove(peerId);
            _peers.Remove(model);
            model.Dispose();
        }

        await _state.RemovePeerAsync(peerId, ct);
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

        model.Track(d1);
        model.Track(d2);
        model.Track(d3);
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
