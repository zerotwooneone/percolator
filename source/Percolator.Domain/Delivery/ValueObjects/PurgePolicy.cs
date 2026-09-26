namespace Percolator.Domain.Delivery.ValueObjects;

public readonly record struct PurgePolicy(
    TimeSpan DefaultTtl,
    int MaxRetainedEnvelopes)
{
    public static PurgePolicy Default => new(
        DefaultTtl: TimeSpan.FromDays(7),
        MaxRetainedEnvelopes: 10_000);
}
