namespace Percolator.Cryptography;

/// <summary>
/// UUID that uniquely identifies an identity.
/// </summary>
public record CryptoPublicIdentityId
{
    public Guid Value { get; }

    public CryptoPublicIdentityId(Guid value)
    {
        if (value == Guid.Empty)
            throw new ArgumentException("CryptoPublicIdentityId cannot be empty.", nameof(value));
        Value = value;
    }

    public static CryptoPublicIdentityId NewId() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString();
}
