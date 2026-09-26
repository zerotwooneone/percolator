namespace Percolator.Domain.Delivery.ValueObjects;

public readonly record struct BlindedRoutingToken(Guid Value) : IEquatable<BlindedRoutingToken>
{
    public static BlindedRoutingToken New() => new(Guid.NewGuid());

    public bool IsEmpty => Value == Guid.Empty;

    public override string ToString() => Value.ToString();
}
