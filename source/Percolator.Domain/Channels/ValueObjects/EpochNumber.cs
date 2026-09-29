namespace Percolator.Domain.Channels.ValueObjects;

public readonly record struct EpochNumber(uint Value) : IComparable<EpochNumber>, IEquatable<EpochNumber>
{
    public static EpochNumber Genesis => new(0);

    public EpochNumber Next() => new(Value + 1);

    public int CompareTo(EpochNumber other) => Value.CompareTo(other.Value);

    public override string ToString() => Value.ToString();
}
