using System;

namespace Percolator.Network.ValueObjects;

public sealed class EndpointFreshness
{
    public DateTimeOffset FirstSeenUtc { get; }
    public DateTimeOffset LastSeenUtc { get; private set; }

    public EndpointFreshness(DateTimeOffset firstSeenUtc)
    {
        FirstSeenUtc = firstSeenUtc;
        LastSeenUtc = firstSeenUtc;
    }

    public void Observe(DateTimeOffset now)
    {
        if (now > LastSeenUtc)
        {
            LastSeenUtc = now;
        }
    }

    public double Score(DateTimeOffset now)
    {
        var delta = now - LastSeenUtc;
        var seconds = delta.TotalSeconds;
        return seconds < 0 ? 0 : seconds;
    }
}
