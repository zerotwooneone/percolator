using Percolator.Domain.Common;

namespace Percolator.Apps.Discovery.Features.PrivateRendezvous;

/// <summary>
/// Value object representing a network epoch or day window for ratcheting rendezvous tokens.
/// Invariants:
/// - EpochDays must be non-negative.
/// - Unwrapping is explicit via .Value.
/// - Zero implicit/explicit operators.
/// </summary>
public readonly record struct DiscoveryEpoch : IEquatable<DiscoveryEpoch>, IComparable<DiscoveryEpoch>
{
    public long Value { get; }

    public DiscoveryEpoch(long value)
    {
        if (value < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "DiscoveryEpoch value cannot be negative.");
        }

        Value = value;
    }

    public static DomainResult<DiscoveryEpoch> Create(long value)
    {
        if (value < 0)
        {
            return DomainResult<DiscoveryEpoch>.Failure(new DomainError(
                "INVALID_EPOCH", "DiscoveryEpoch value cannot be negative."));
        }

        return DomainResult<DiscoveryEpoch>.Success(new DiscoveryEpoch(value));
    }

    public static DiscoveryEpoch FromUtc(DateTimeOffset utcTimestamp)
    {
        // Epoch day based on Unix epoch (1970-01-01)
        long days = utcTimestamp.ToUnixTimeSeconds() / 86400;
        return new DiscoveryEpoch(days >= 0 ? days : 0);
    }

    public int CompareTo(DiscoveryEpoch other) => Value.CompareTo(other.Value);

    public override string ToString() => Value.ToString();
}
