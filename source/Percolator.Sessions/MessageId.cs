namespace Percolator.Sessions;

public record MessageId
{
    public Guid Value { get; }

    public MessageId(Guid value)
    {
        if (value == Guid.Empty)
            throw new ArgumentException("Message ID cannot be empty.", nameof(value));
        Value = value;
    }

    public static MessageId NewId() => new(Guid.NewGuid());
}
