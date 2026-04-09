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

        peer.DisplayName.Subscribe(_ => _dirty.OnNext(Unit.Default)).AddTo(ref _bag);
        peer.IsOnline.Subscribe(_ => _dirty.OnNext(Unit.Default)).AddTo(ref _bag);
        peer.IsRelayCapable.Subscribe(_ => _dirty.OnNext(Unit.Default)).AddTo(ref _bag);

        peer.ConnectionMode.Subscribe(_ => _dirty.OnNext(Unit.Default)).AddTo(ref _bag);
        peer.Host.Subscribe(_ => _dirty.OnNext(Unit.Default)).AddTo(ref _bag);
        peer.Port.Subscribe(_ => _dirty.OnNext(Unit.Default)).AddTo(ref _bag);
        peer.RelayPeerId.Subscribe(_ => _dirty.OnNext(Unit.Default)).AddTo(ref _bag);

        peer.UiState.Subscribe(_ => _dirty.OnNext(Unit.Default)).AddTo(ref _bag);
        peer.InboundReverseSignalPendingCorrelationId.Subscribe(_ => _dirty.OnNext(Unit.Default)).AddTo(ref _bag);
        peer.TargetPublicKeyHash.Subscribe(_ => _dirty.OnNext(Unit.Default)).AddTo(ref _bag);
        peer.SelectedRouteMode.Subscribe(_ => _dirty.OnNext(Unit.Default)).AddTo(ref _bag);
        peer.DirectEndpoint.Subscribe(_ => _dirty.OnNext(Unit.Default)).AddTo(ref _bag);
        peer.RelayHostPeerId.Subscribe(_ => _dirty.OnNext(Unit.Default)).AddTo(ref _bag);
        peer.Phase.Subscribe(_ => _dirty.OnNext(Unit.Default)).AddTo(ref _bag);
        peer.NotUntilUtc.Subscribe(_ => _dirty.OnNext(Unit.Default)).AddTo(ref _bag);
        peer.LastError.Subscribe(_ => _dirty.OnNext(Unit.Default)).AddTo(ref _bag);
        peer.HandshakeAttemptsVersion.Subscribe(_ => _dirty.OnNext(Unit.Default)).AddTo(ref _bag);

        peer.PendingStandardHandshakeToMainResponderPublicKeyHash.Subscribe(_ => _dirty.OnNext(Unit.Default)).AddTo(ref _bag);
        peer.PendingStandardHandshakeToMainTemporarySessionId.Subscribe(_ => _dirty.OnNext(Unit.Default)).AddTo(ref _bag);

        peer.SessionsMutable.ObserveChanged().Subscribe(_ => _dirty.OnNext(Unit.Default)).AddTo(ref _bag);

        peer.SignedPreKeysMutable.ObserveChanged().Subscribe(_ => _dirty.OnNext(Unit.Default)).AddTo(ref _bag);

        peer.OutboundInvitesMutable.ObserveChanged().Subscribe(_ => _dirty.OnNext(Unit.Default)).AddTo(ref _bag);

        peer.PendingInviteHandshakeResponsesMutable.ObserveChanged().Subscribe(_ => _dirty.OnNext(Unit.Default)).AddTo(ref _bag);

        peer.PendingInboundStandardSignalHellosMutable.ObserveChanged().Subscribe(_ => _dirty.OnNext(Unit.Default)).AddTo(ref _bag);
    }

    public SimulatedPeerModel Peer { get; }

    public Observable<Unit> Dirty => _dirty;

    public void Dispose()
    {
        _bag.Dispose();
        _dirty.Dispose();
    }
}
