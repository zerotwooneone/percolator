namespace Percolator.Infrastructure.Persistence;

/// <summary>
/// Database object for storing authenticated peer egress jobs.
/// </summary>
public sealed class AuthenticatedPeerEgressJobDbo
{
    public uint JobId { get; set; }
    public uint DestinationPeerId { get; set; }
    public int RoutePreference { get; set; }
    public byte[] PayloadBytes { get; set; } = null!;
    public int AttemptCount { get; set; }
    public DateTimeOffset NextAttemptUtc { get; set; }
    public bool IsSent { get; set; }
    public bool IsPermanentlyFailed { get; set; }
}
