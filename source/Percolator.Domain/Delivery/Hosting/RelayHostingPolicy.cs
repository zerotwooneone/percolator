namespace Percolator.Domain.Delivery.Hosting;

public sealed record RelayHostingPolicy(
    bool IsAcceptingPreKeys = true,
    int MaxOneTimePreKeysPerIdentity = RelayHostingPolicy.DefaultMaxOneTimePreKeys)
{
    public const int DefaultMaxOneTimePreKeys = 100;

    public static RelayHostingPolicy Default { get; } = new();

    public int MaxOneTimePreKeysPerIdentity { get; init; } = 
        MaxOneTimePreKeysPerIdentity > 0 ? MaxOneTimePreKeysPerIdentity : DefaultMaxOneTimePreKeys;
}
