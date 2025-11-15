namespace Percolator.Cryptography;

public record PendingSessionId
{
    public Guid Value { get; }

    public PendingSessionId(Guid value)
    {
        if (value == Guid.Empty)
            throw new ArgumentException("PendingSessionId cannot be empty.", nameof(value));
        Value = value;
    }

    public static PendingSessionId NewId() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString();
}
