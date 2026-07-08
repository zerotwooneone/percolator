namespace Percolator.Infrastructure.Persistence;

public class PeerRouteCandidateDbo
{
    public long Id { get; set; }
    public uint SelfIdentityId { get; set; }
    public uint RemotePeerId { get; set; }
    public int RouteKind { get; set; }
    public string? EndpointHost { get; set; }
    public int? EndpointPort { get; set; }
    public uint? RelayHostPeerId { get; set; }
    public DateTimeOffset ObservedAtUtc { get; set; }
    public DateTimeOffset? LastAttemptAtUtc { get; set; }
    public DateTimeOffset? LastSuccessAtUtc { get; set; }
    public int AttemptCount { get; set; }
    public string? LastError { get; set; }
    public string Source { get; set; } = null!;
}
