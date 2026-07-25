using Percolator.Network.ValueObjects;

namespace Percolator.Network.Egress;

/// <summary>
/// Aggregate root for anonymous egress jobs to Relays.
/// </summary>
public sealed class AnonymousRelayEgressJob
{
    public EgressJobId JobId { get; }
    public NetworkPeerId RelayPeerId { get; }
    public NetworkPayloadBytes PayloadBytes { get; }
    public DeliveryAttemptCount Attempts { get; private set; }
    public DateTimeOffset NextAttemptUtc { get; private set; }
    public bool IsSent { get; private set; }
    public bool IsPermanentlyFailed { get; private set; }

    private const int MaxAttempts = 10;

    public AnonymousRelayEgressJob(
        EgressJobId jobId,
        NetworkPeerId relayPeerId,
        NetworkPayloadBytes payloadBytes,
        DateTimeOffset nextAttemptUtc)
    {
        JobId = jobId;
        RelayPeerId = relayPeerId;
        PayloadBytes = payloadBytes;
        Attempts = new DeliveryAttemptCount(0);
        NextAttemptUtc = nextAttemptUtc;
        IsSent = false;
        IsPermanentlyFailed = false;
    }

    /// <summary>
    /// Records a delivery failure and schedules the next attempt with exponential backoff.
    /// </summary>
    public void RecordFailure(DateTimeOffset now)
    {
        if (IsSent || IsPermanentlyFailed)
        {
            return;
        }

        Attempts = new DeliveryAttemptCount(Attempts.Value + 1);
        
        if (Attempts.Value >= MaxAttempts)
        {
            IsPermanentlyFailed = true;
            return;
        }

        // Exponential backoff: now + 2^Attempts seconds
        var backoffSeconds = TimeSpan.FromSeconds(Math.Pow(2, Attempts.Value));
        NextAttemptUtc = now + backoffSeconds;
    }

    /// <summary>
    /// Marks the job as successfully sent.
    /// </summary>
    public void MarkSent()
    {
        if (IsPermanentlyFailed)
        {
            return;
        }

        IsSent = true;
    }
}
