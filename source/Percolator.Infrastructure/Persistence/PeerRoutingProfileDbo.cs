using Percolator.Network;

namespace Percolator.Infrastructure.Persistence;

public class PeerRoutingProfileDbo
{
    public PeerId PeerId { get; set; }
    public byte[]? DirectMessagePublicKey { get; set; }
    public int ReachabilityStatus { get; set; }
    public DateTimeOffset? ReachabilityLastChangeUtc { get; set; }
}
