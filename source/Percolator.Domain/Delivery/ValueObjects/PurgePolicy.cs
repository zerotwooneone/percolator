namespace Percolator.Domain.Delivery.ValueObjects;

public readonly record struct PurgePolicy(
    TimeSpan DefaultTtl,
    int MaxRetainedEnvelopes,
    int MaxEnvelopeSizeBytes = PurgePolicy.DefaultMaxEnvelopeSizeBytes)
{
    public const int DefaultMaxEnvelopeSizeBytes = 256 * 1024; // 256 KB

    public static PurgePolicy Default => new(
        DefaultTtl: TimeSpan.FromDays(7),
        MaxRetainedEnvelopes: 10_000,
        MaxEnvelopeSizeBytes: DefaultMaxEnvelopeSizeBytes);
}
