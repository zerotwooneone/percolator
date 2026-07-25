using Percolator.Network.ValueObjects;

namespace Percolator.Network.Egress;

/// <summary>
/// Aggregate root for authenticated egress jobs to specific peers.
/// </summary>
public sealed class AuthenticatedPeerEgressJob
{
    public EgressJobId JobId { get; }
    public NetworkPeerId DestinationPeerId { get; }
    public RoutePreference RoutePreference { get; }
    public NetworkPayloadBytes PayloadBytes { get; }
    public DeliveryAttemptCount Attempts { get; private set; }
    public DateTimeOffset NextAttemptUtc { get; private set; }
    public bool IsSent { get; private set; }
    public bool IsPermanentlyFailed { get; private set; }

    private const int MaxAttempts = 10;

    public AuthenticatedPeerEgressJob(
        EgressJobId jobId,
        NetworkPeerId destinationPeerId,
        RoutePreference routePreference,
        NetworkPayloadBytes payloadBytes,
        DateTimeOffset nextAttemptUtc)
    {
        JobId = jobId;
        DestinationPeerId = destinationPeerId;
        RoutePreference = routePreference;
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
