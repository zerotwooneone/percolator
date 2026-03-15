namespace Percolator.Infrastructure.Persistence;

public class PeerRoutingProfileDbo
{
    public Guid PeerId { get; set; }
    public byte[]? DirectMessagePublicKey { get; set; }
    public int ReachabilityStatus { get; set; }
    public DateTimeOffset? ReachabilityLastChangeUtc { get; set; }
}
