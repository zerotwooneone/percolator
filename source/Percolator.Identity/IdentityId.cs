namespace Percolator.Identity;

/// <summary>
/// UUID that uniquely identifies an identity.
/// </summary>
public record PublicIdentityId
{
    public Guid Value { get; }

    public PublicIdentityId(Guid value)
    {
        if (value == Guid.Empty)
            throw new ArgumentException("Peer ID cannot be empty.", nameof(value));
        Value = value;
    }

    public static PublicIdentityId NewId() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString();
}