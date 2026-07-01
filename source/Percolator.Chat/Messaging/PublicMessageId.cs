namespace Percolator.Chat.Messaging;

public record PublicMessageId
{
    public Guid Value { get; }

    public PublicMessageId(Guid value)
    {
        if (value == Guid.Empty)
            throw new ArgumentException("PublicIdentityId cannot be empty.", nameof(value));
        Value = value;
    }

    public static PublicMessageId NewId() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString();
}