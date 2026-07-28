namespace Percolator.Infrastructure.Network.Egress;

public class RelayEgressJobDbo
{
    public Guid JobId { get; set; }
    public uint DestinationPeerId { get; set; }
    public byte[] PayloadBytes { get; set; } = [];
    public int AttemptCount { get; set; }
    public long NextAttemptUtc { get; set; }
}
