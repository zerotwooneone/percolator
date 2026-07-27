using Percolator.Network.ValueObjects;

namespace Percolator.Network.Egress;

/// <summary>
/// Aggregate root for generalized egress jobs that the Relay must deliver to connected clients.
/// The payload wraps ServerRelayStream messages and can accommodate various message types (group messages, 1:1 messages, etc.).
/// </summary>
public sealed class RelayEgressJob
{
    public Guid JobId { get; }
    public NetworkPeerId DestinationPeerId { get; }
    public byte[] PayloadBytes { get; }
    public int AttemptCount { get; private set; }
    public DateTimeOffset NextAttemptUtc { get; private set; }

    private const int MaxAttempts = 10;

    public RelayEgressJob(
        Guid jobId,
        NetworkPeerId destinationPeerId,
        byte[] payloadBytes,
        DateTimeOffset nextAttemptUtc)
    {
        JobId = jobId;
        DestinationPeerId = destinationPeerId;
        PayloadBytes = payloadBytes;
        AttemptCount = 0;
        NextAttemptUtc = nextAttemptUtc;
    }

    /// <summary>
    /// Records a delivery failure and schedules the next attempt with exponential backoff.
    /// </summary>
    public void RecordFailure(DateTimeOffset now)
    {
        AttemptCount++;
        
        if (AttemptCount >= MaxAttempts)
        {
            // Mark as permanently failed - could add a property for this if needed
            return;
        }

        // Exponential backoff: now + 2^AttemptCount seconds
        var backoffSeconds = TimeSpan.FromSeconds(Math.Pow(2, AttemptCount));
        NextAttemptUtc = now + backoffSeconds;
    }

    /// <summary>
    /// Increments the attempt count and updates the next attempt time.
    /// </summary>
    public void ScheduleNextAttempt(DateTimeOffset nextAttemptUtc)
    {
        AttemptCount++;
        NextAttemptUtc = nextAttemptUtc;
    }
}
