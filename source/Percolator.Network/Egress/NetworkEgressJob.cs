using Percolator.Network.ValueObjects;

namespace Percolator.Network.Egress;

public enum RoutePreference
{
    Direct,
    Relay,
    Any
}

public enum PayloadType
{
    Group,
    Opaque1to1
}

public sealed class NetworkEgressJob
{
    public EgressJobId JobId { get; }
    public NetworkPeerId DestinationPeerId { get; }
    public RoutePreference RoutePreference { get; }
    public PayloadType PayloadType { get; }
    public NetworkPayloadBytes PayloadBytes { get; }
    public int AttemptCount { get; private set; }
    public DateTimeOffset NextAttemptUtc { get; private set; }
    public bool IsSent { get; private set; }
    public bool IsPermanentlyFailed { get; private set; }

    private const int MaxAttempts = 10;

    public NetworkEgressJob(
        EgressJobId jobId = default,
        NetworkPeerId destinationPeerId = default,
        RoutePreference routePreference = default,
        PayloadType payloadType = default,
        NetworkPayloadBytes payloadBytes = default,
        DateTimeOffset nextAttemptUtc = default)
    {
        JobId = jobId;
        DestinationPeerId = destinationPeerId;
        RoutePreference = routePreference;
        PayloadType = payloadType;
        PayloadBytes = payloadBytes;
        AttemptCount = 0;
        NextAttemptUtc = nextAttemptUtc;
        IsSent = false;
        IsPermanentlyFailed = false;
    }

    public void RecordFailure(DateTimeOffset now)
    {
        if (IsSent || IsPermanentlyFailed)
        {
            return;
        }

        AttemptCount++;
        
        if (AttemptCount >= MaxAttempts)
        {
            IsPermanentlyFailed = true;
            return;
        }

        // Exponential backoff: UtcNow + 2^AttemptCount seconds
        var backoffSeconds = TimeSpan.FromSeconds(Math.Pow(2, AttemptCount));
        NextAttemptUtc = now + backoffSeconds;
    }

    public void MarkSent()
    {
        if (IsPermanentlyFailed)
        {
            return;
        }

        IsSent = true;
    }
}
