using ObservableCollections;
using R3;

namespace Desktop.Wpf.Features.Simulator.Tracking;

public sealed class SimulatedPeerRuntimeTracker : IDisposable
{
    private DisposableBag _bag;
    private readonly Subject<Unit> _dirty = new();

    public SimulatedPeerRuntimeTracker(SimulatedPeerModel peer)
    {
        Peer = peer ?? throw new ArgumentNullException(nameof(peer));

        peer.UiState.Subscribe(_ => _dirty.OnNext(Unit.Default)).AddTo(ref _bag);
        peer.PendingCorrelationId.Subscribe(_ => _dirty.OnNext(Unit.Default)).AddTo(ref _bag);
        peer.TargetPublicKeyHash.Subscribe(_ => _dirty.OnNext(Unit.Default)).AddTo(ref _bag);
        peer.SelectedRouteMode.Subscribe(_ => _dirty.OnNext(Unit.Default)).AddTo(ref _bag);
        peer.DirectEndpoint.Subscribe(_ => _dirty.OnNext(Unit.Default)).AddTo(ref _bag);
        peer.RelayHostPeerId.Subscribe(_ => _dirty.OnNext(Unit.Default)).AddTo(ref _bag);
        peer.Phase.Subscribe(_ => _dirty.OnNext(Unit.Default)).AddTo(ref _bag);
        peer.NotUntilUtc.Subscribe(_ => _dirty.OnNext(Unit.Default)).AddTo(ref _bag);
        peer.LastError.Subscribe(_ => _dirty.OnNext(Unit.Default)).AddTo(ref _bag);
        peer.HandshakeAttemptsVersion.Subscribe(_ => _dirty.OnNext(Unit.Default)).AddTo(ref _bag);

        peer.SessionsMutable.ObserveAdd().Subscribe(_ => _dirty.OnNext(Unit.Default)).AddTo(ref _bag);
        peer.SessionsMutable.ObserveRemove().Subscribe(_ => _dirty.OnNext(Unit.Default)).AddTo(ref _bag);
        peer.SessionsMutable.ObserveReplace().Subscribe(_ => _dirty.OnNext(Unit.Default)).AddTo(ref _bag);
        peer.SessionsMutable.ObserveReset().Subscribe(_ => _dirty.OnNext(Unit.Default)).AddTo(ref _bag);

        peer.SignedPreKeysMutable.ObserveAdd().Subscribe(_ => _dirty.OnNext(Unit.Default)).AddTo(ref _bag);
        peer.SignedPreKeysMutable.ObserveRemove().Subscribe(_ => _dirty.OnNext(Unit.Default)).AddTo(ref _bag);
        peer.SignedPreKeysMutable.ObserveReplace().Subscribe(_ => _dirty.OnNext(Unit.Default)).AddTo(ref _bag);
        peer.SignedPreKeysMutable.ObserveReset().Subscribe(_ => _dirty.OnNext(Unit.Default)).AddTo(ref _bag);

        peer.OutboundInvitesMutable.ObserveAdd().Subscribe(_ => _dirty.OnNext(Unit.Default)).AddTo(ref _bag);
        peer.OutboundInvitesMutable.ObserveRemove().Subscribe(_ => _dirty.OnNext(Unit.Default)).AddTo(ref _bag);
        peer.OutboundInvitesMutable.ObserveReplace().Subscribe(_ => _dirty.OnNext(Unit.Default)).AddTo(ref _bag);
        peer.OutboundInvitesMutable.ObserveReset().Subscribe(_ => _dirty.OnNext(Unit.Default)).AddTo(ref _bag);

        peer.PendingInviteHandshakeResponsesMutable.ObserveAdd().Subscribe(_ => _dirty.OnNext(Unit.Default)).AddTo(ref _bag);
        peer.PendingInviteHandshakeResponsesMutable.ObserveRemove().Subscribe(_ => _dirty.OnNext(Unit.Default)).AddTo(ref _bag);
        peer.PendingInviteHandshakeResponsesMutable.ObserveReplace().Subscribe(_ => _dirty.OnNext(Unit.Default)).AddTo(ref _bag);
        peer.PendingInviteHandshakeResponsesMutable.ObserveReset().Subscribe(_ => _dirty.OnNext(Unit.Default)).AddTo(ref _bag);
    }

    public SimulatedPeerModel Peer { get; }

    public Observable<Unit> Dirty => _dirty;

    public void Dispose()
    {
        _bag.Dispose();
        _dirty.Dispose();
    }
}
