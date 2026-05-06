using Desktop.Wpf.Features.Sessions.Queries;
using R3;

namespace Desktop.Wpf.Features.Sessions.Models;

public sealed class PeerConnectionModel : IDisposable
{
    private DisposableBag _bag;
    private readonly ReactiveProperty<string> _displayName;
    private readonly ReactiveProperty<string> _initials;
    private readonly ReactiveProperty<PeerConnectionStatus> _status;
    private readonly ReactiveProperty<DateTimeOffset> _lastActivityUtc;

    public PeerConnectionModel(
        PeerConnectionKey key,
        Guid? peerId,
        string displayName,
        string initials,
        PeerConnectionStatus status,
        DateTimeOffset lastActivityUtc)
    {
        Key = key;
        PeerId = peerId;

        _displayName = new ReactiveProperty<string>(displayName);
        _initials = new ReactiveProperty<string>(initials);
        _status = new ReactiveProperty<PeerConnectionStatus>(status);
        _lastActivityUtc = new ReactiveProperty<DateTimeOffset>(lastActivityUtc);
    }

    public PeerConnectionKey Key { get; }
    public Guid? PeerId { get; }

    public ReadOnlyReactiveProperty<string> DisplayName => _displayName;
    public ReadOnlyReactiveProperty<string> Initials => _initials;
    public ReadOnlyReactiveProperty<PeerConnectionStatus> Status => _status;
    public ReadOnlyReactiveProperty<DateTimeOffset> LastActivityUtc => _lastActivityUtc;

    internal void UpdateFromSnapshot(Queries.PeerConnectionStateSnapshot snapshot)
    {
        _displayName.Value = snapshot.DisplayName;
        _initials.Value = snapshot.Initials;
        _status.Value = snapshot.Status;
        _lastActivityUtc.Value = snapshot.LastActivityUtc;
    }

    public void Dispose() => _bag.Dispose();
}
