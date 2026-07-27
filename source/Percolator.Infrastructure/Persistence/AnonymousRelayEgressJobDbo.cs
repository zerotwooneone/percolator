namespace Percolator.Infrastructure.Persistence;

/// <summary>
/// Database object for storing anonymous Relay egress jobs.
/// </summary>
public sealed class AnonymousRelayEgressJobDbo
{
    public uint JobId { get; set; }
    public uint RelayPeerId { get; set; }
    public byte[] PayloadBytes { get; set; } = null!;
    public int AttemptCount { get; set; }
    public DateTimeOffset NextAttemptUtc { get; set; }
    public bool IsSent { get; set; }
    public bool IsPermanentlyFailed { get; set; }
}
