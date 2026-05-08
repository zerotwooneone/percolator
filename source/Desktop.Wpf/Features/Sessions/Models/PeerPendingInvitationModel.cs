using Percolator.Application.Sessions;
using Percolator.Identity;
using R3;

namespace Desktop.Wpf.Features.Sessions.Models;

public sealed class PeerPendingInvitationModel : IDisposable
{
    private readonly DisposableBag _bag;
    private readonly ReactiveProperty<string> _peerName;
    private readonly ReactiveProperty<bool> _isRelayed;
    private readonly ReactiveProperty<DateTimeOffset?> _expiresAtUtc;
    private readonly ReactiveProperty<string?> _relayPeerName;
    private readonly ReactiveProperty<string?> _relayEndpoint;

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
        CreatedAtUtc = createdAtUtc;

        _peerName = new ReactiveProperty<string>(peerName);
        _isRelayed = new ReactiveProperty<bool>(isRelayed);
        _expiresAtUtc = new ReactiveProperty<DateTimeOffset?>(null);
        _relayPeerName = new ReactiveProperty<string?>(null);
        _relayEndpoint = new ReactiveProperty<string?>(null);
    }

    public Guid PendingSessionId { get; }
    public Guid RequestCorrelationId { get; }
    public Guid PeerId { get; }
    public DateTimeOffset CreatedAtUtc { get; }
    public SelfId SelfIdentityId { get; private set; }

    public string? InviterFingerprintHex { get; private set; }
    public Guid? RelayPeerId { get; private set; }

    public ReadOnlyReactiveProperty<string> PeerName => _peerName;
    public ReadOnlyReactiveProperty<bool> IsRelayed => _isRelayed;
    public ReadOnlyReactiveProperty<DateTimeOffset?> ExpiresAtUtc => _expiresAtUtc;
    public ReadOnlyReactiveProperty<string?> RelayPeerName => _relayPeerName;
    public ReadOnlyReactiveProperty<string?> RelayEndpoint => _relayEndpoint;

    internal void UpdateFromSnapshot(PendingInboundSnapshot snapshot)
    {
        _peerName.Value = snapshot.PeerName;
        _isRelayed.Value = snapshot.IsRelayed;
        _expiresAtUtc.Value = snapshot.ExpiresAtUtc;
        _relayPeerName.Value = snapshot.RelayPeerName;
        _relayEndpoint.Value = snapshot.RelayEndpoint;

        InviterFingerprintHex = snapshot.InviterFingerprintHex;
        RelayPeerId = snapshot.RelayPeerId;
        SelfIdentityId = snapshot.SelfIdentityId;
    }

    public void Dispose() => _bag.Dispose();
}
