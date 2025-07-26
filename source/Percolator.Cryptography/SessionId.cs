namespace Percolator.Cryptography;

public record SessionId
{
    public Guid Value { get; }

    public SessionId(Guid value)
    {
        if (value == Guid.Empty)
            throw new ArgumentException("Peer ID cannot be empty.", nameof(value));
        Value = value;
    }

    public static SessionId NewId() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString();
}
