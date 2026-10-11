using Percolator.Domain.Common;

namespace Percolator.Apps.Discovery.Features.NearbyPeerDiscovery;

/// <summary>
/// Strongly typed result limit value object preventing excessive memory allocation or denial-of-service.
/// Invariants:
/// - 1 <= Limit <= 100
/// - Default: 50
/// - Zero implicit/explicit operators.
/// </summary>
public readonly record struct DiscoveryResultLimit : IEquatable<DiscoveryResultLimit>, IComparable<DiscoveryResultLimit>
{
    public const int Min = 1;
    public const int Max = 100;
    public const int DefaultValue = 50;

    public int Value { get; }

    public DiscoveryResultLimit(int value)
    {
        if (value is < Min or > Max)
        {
            throw new ArgumentOutOfRangeException(nameof(value), $"Limit must be between {Min} and {Max}.");
        }

        Value = value;
    }

    public static DiscoveryResultLimit Default => new(DefaultValue);

    public static DomainResult<DiscoveryResultLimit> Create(int value)
    {
        if (value is < Min or > Max)
        {
            return DomainResult<DiscoveryResultLimit>.Failure(new DomainError(
                "INVALID_LIMIT", $"Discovery result limit must be between {Min} and {Max}."));
        }

        return DomainResult<DiscoveryResultLimit>.Success(new DiscoveryResultLimit(value));
    }

    public static DiscoveryResultLimit Clamp(int value)
    {
        if (value < Min) return new DiscoveryResultLimit(Min);
        if (value > Max) return new DiscoveryResultLimit(Max);
        return new DiscoveryResultLimit(value);
    }

    public int CompareTo(DiscoveryResultLimit other) => Value.CompareTo(other.Value);

    public override string ToString() => Value.ToString();
}
