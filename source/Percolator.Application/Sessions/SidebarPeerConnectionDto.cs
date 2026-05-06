namespace Percolator.Application.Sessions;

public sealed class SidebarPeerConnectionDto
{
    public int SelfIdentityId { get; init; }
    public SidebarPeerConnectionKeyType KeyType { get; init; }
    public Guid KeyValue { get; init; }
    public Guid? PeerId { get; init; }
    public string DisplayName { get; init; } = string.Empty;
    public string Initials { get; init; } = string.Empty;
    public SidebarPeerConnectionStatus Status { get; init; }
    public Guid? RelayHostPeerId { get; init; }
    public DateTimeOffset LastActivityUtc { get; init; }
}
