namespace Percolator.Cryptography;

public sealed class EstablishmentPlan
{
    public bool HasOneTimePreKey { get; }

    public EstablishmentPlan(bool hasOneTimePreKey)
    {
        HasOneTimePreKey = hasOneTimePreKey;
    }
}
