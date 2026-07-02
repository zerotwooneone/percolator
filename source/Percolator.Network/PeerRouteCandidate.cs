namespace Percolator.Network;

public enum RouteKind
{
    Direct = 0,
    Relayed = 1
}

public sealed class PeerRouteCandidate
{
    public long Id { get; set; }
    public uint SelfIdentityId { get; set; }
    public PeerId RemotePeerId { get; set; }
    public RouteKind RouteKind { get; set; }
    public string? EndpointHost { get; set; }
    public int? EndpointPort { get; set; }
    public PeerId? RelayHostPeerId { get; set; }
    public DateTimeOffset ObservedAtUtc { get; set; }
    public DateTimeOffset? LastAttemptAtUtc { get; set; }
    public DateTimeOffset? LastSuccessAtUtc { get; set; }
    public int AttemptCount { get; set; }
    public string? LastError { get; set; }
    public string Source { get; set; } = null!;
}
