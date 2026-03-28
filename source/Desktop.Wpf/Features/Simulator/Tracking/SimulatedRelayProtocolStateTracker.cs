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

        relay.MessageQueue.ObserveAdd().Subscribe(_ => _dirty.OnNext(Unit.Default)).AddTo(ref _bag);
        relay.MessageQueue.ObserveRemove().Subscribe(_ => _dirty.OnNext(Unit.Default)).AddTo(ref _bag);
        relay.MessageQueue.ObserveReplace().Subscribe(_ => _dirty.OnNext(Unit.Default)).AddTo(ref _bag);
        relay.MessageQueue.ObserveReset().Subscribe(_ => _dirty.OnNext(Unit.Default)).AddTo(ref _bag);
    }

    public SimulatedRelayModel Relay { get; }

    public Observable<Unit> Dirty => _dirty;

    public void Dispose()
    {
        _bag.Dispose();
        _dirty.Dispose();
    }
}
