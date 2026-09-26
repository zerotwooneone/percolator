namespace Percolator.Domain.Identities.ValueObjects;

public readonly record struct PublicIdentityId(Guid Value) : IEquatable<PublicIdentityId>
{
    public static PublicIdentityId New() => new(Guid.NewGuid());

    public bool IsEmpty => Value == Guid.Empty;

    public override string ToString() => Value.ToString();
}
