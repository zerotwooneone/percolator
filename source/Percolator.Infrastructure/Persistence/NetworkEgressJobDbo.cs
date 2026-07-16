namespace Percolator.Infrastructure.Persistence;

/// <summary>
/// Database object for storing network egress jobs (serialized network payloads waiting to be transmitted).
/// Separated from domain event outbox to maintain clean architecture boundaries.
/// </summary>
public sealed class NetworkEgressJobDbo
{
    public uint JobId { get; set; }
    public uint DestinationPeerId { get; set; }
    public int RoutePreference { get; set; } // 0=Direct, 1=Relay, 2=Any
    public int PayloadType { get; set; } // 0=Group, 1=Opaque1to1
    public byte[] PayloadBytes { get; set; } = null!;
    public int AttemptCount { get; set; }
    public DateTimeOffset NextAttemptUtc { get; set; }
    public bool IsSent { get; set; }
    public bool IsPermanentlyFailed { get; set; }
}
