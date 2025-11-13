using System;

namespace Percolator.Network.ValueObjects;

public sealed class FreshnessPolicy
{
    public TimeSpan HalfLife { get; }
    public TimeSpan PruneAfter { get; }

    public FreshnessPolicy(TimeSpan halfLife, TimeSpan pruneAfter)
    {
        HalfLife = halfLife;
        PruneAfter = pruneAfter;
    }
}
