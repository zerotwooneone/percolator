namespace Percolator.Network.ValueObjects;

public enum ReachabilityStatus
{
    Unknown = 0,
    Online = 1,
    Offline = 2,
    Degraded = 3
}

public sealed class Reachability
{
    public ReachabilityStatus Status { get; private set; }
    public DateTimeOffset LastChangeUtc { get; private set; }

    public Reachability()
    {
        Status = ReachabilityStatus.Unknown;
        LastChangeUtc = DateTimeOffset.MinValue;
    }

    public void TransitionTo(ReachabilityStatus newStatus, DateTimeOffset now)
    {
        Status = newStatus;
        LastChangeUtc = now;
    }
}
