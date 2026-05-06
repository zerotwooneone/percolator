using Desktop.Wpf.Features.Sessions.Models;

namespace Desktop.Wpf.Features.Sessions.Queries;

public enum PeerConnectionStatus
{
    Direct,
    Relay,
    Group,
    PendingOutbound
}

public sealed record PeerConnectionStateSnapshot(
    PeerConnectionKey Key,
    Guid? PeerId,
    string DisplayName,
    string Initials,
    PeerConnectionStatus Status,
    Guid? RelayHostPeerId,
    DateTimeOffset LastActivityUtc
);
