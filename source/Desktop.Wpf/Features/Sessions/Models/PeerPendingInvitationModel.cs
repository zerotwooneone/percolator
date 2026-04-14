using R3;

namespace Desktop.Wpf.Features.Sessions.Models;

public sealed class PeerPendingInvitationModel : IDisposable
{
    private readonly DisposableBag _bag;
    private readonly ReactiveProperty<string> _peerName;
    private readonly ReactiveProperty<bool> _isRelayed;

    public PeerPendingInvitationModel(
        Guid pendingSessionId,
        Guid requestCorrelationId,
        Guid peerId,
        string peerName,
        bool isRelayed,
        DateTimeOffset createdAtUtc)
    {
        PendingSessionId = pendingSessionId;
        RequestCorrelationId = requestCorrelationId;
        PeerId = peerId;

        _peerName = new ReactiveProperty<string>(peerName);
        _isRelayed = new ReactiveProperty<bool>(isRelayed);
    }

    public Guid PendingSessionId { get; }
    public Guid RequestCorrelationId { get; }
    public Guid PeerId { get; }

    public ReadOnlyReactiveProperty<string> PeerName => _peerName;
    public ReadOnlyReactiveProperty<bool> IsRelayed => _isRelayed;

    internal void UpdateFromSnapshot(Queries.PendingInboundSnapshot snapshot)
    {
        _peerName.Value = snapshot.PeerName;
        _isRelayed.Value = snapshot.IsRelayed;
    }

    public void Dispose() => _bag.Dispose();
}
