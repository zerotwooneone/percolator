namespace Desktop.Wpf.Features.Sessions.Queries;

public enum PeerConnectionStatus
{
    Direct,
    Relay,
    Group
}

public sealed record PeerConnectionStateSnapshot(
    Guid ConnectionId,
    Guid PeerId,
    string DisplayName,
    string Initials,
    PeerConnectionStatus Status,
    Guid? RelayHostPeerId,
    DateTimeOffset LastActivityUtc
);
