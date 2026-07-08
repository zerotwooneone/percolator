namespace Percolator.Cryptography.Primitives;

/// <summary>
/// UUID that uniquely identifies an identity in the cryptography domain.
/// This is a copy of PublicIdentityId to maintain domain isolation.
/// </summary>
public record CryptoPublicIdentity
{
    public Guid Value { get; }

    public CryptoPublicIdentity(Guid value)
    {
        if (value == Guid.Empty)
            throw new ArgumentException("CryptoPublicIdentity cannot be empty.", nameof(value));
        Value = value;
    }

    public static CryptoPublicIdentity NewId() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString();
}
