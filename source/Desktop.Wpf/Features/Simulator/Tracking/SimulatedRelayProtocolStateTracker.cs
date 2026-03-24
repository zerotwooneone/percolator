using Desktop.Wpf.Features.Simulator.Models;
using ObservableCollections;
using R3;

namespace Desktop.Wpf.Features.Simulator.Tracking;

public sealed class SimulatedRelayProtocolStateTracker : IDisposable
{
    private DisposableBag _bag;
    private readonly Subject<Unit> _dirty = new();

    public SimulatedRelayProtocolStateTracker(SimulatedRelayModel relay)
    {
        Relay = relay ?? throw new ArgumentNullException(nameof(relay));

        relay.UpstreamToMain.ObserveAdd().Subscribe(_ => _dirty.OnNext(Unit.Default)).AddTo(ref _bag);
        relay.UpstreamToMain.ObserveRemove().Subscribe(_ => _dirty.OnNext(Unit.Default)).AddTo(ref _bag);
        relay.UpstreamToMain.ObserveReplace().Subscribe(_ => _dirty.OnNext(Unit.Default)).AddTo(ref _bag);
        relay.UpstreamToMain.ObserveReset().Subscribe(_ => _dirty.OnNext(Unit.Default)).AddTo(ref _bag);

        relay.DownstreamToPeers.ObserveAdd().Subscribe(_ => _dirty.OnNext(Unit.Default)).AddTo(ref _bag);
        relay.DownstreamToPeers.ObserveRemove().Subscribe(_ => _dirty.OnNext(Unit.Default)).AddTo(ref _bag);
        relay.DownstreamToPeers.ObserveReplace().Subscribe(_ => _dirty.OnNext(Unit.Default)).AddTo(ref _bag);
        relay.DownstreamToPeers.ObserveReset().Subscribe(_ => _dirty.OnNext(Unit.Default)).AddTo(ref _bag);
    }

    public SimulatedRelayModel Relay { get; }

    public Observable<Unit> Dirty => _dirty;

    public void Dispose()
    {
        _bag.Dispose();
        _dirty.Dispose();
    }
}
