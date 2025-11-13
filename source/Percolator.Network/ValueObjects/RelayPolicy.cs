namespace Percolator.Network.ValueObjects;

public sealed class RelayPolicy
{
    public int MaxHops { get; }
    public int MaxCandidates { get; }

    public RelayPolicy(int maxHops = 1, int maxCandidates = 3)
    {
        MaxHops = maxHops;
        MaxCandidates = maxCandidates;
    }
}
