namespace Percolator.Identity;

public record ConversationId
{
    public Guid Value { get; }

    public ConversationId(Guid value)
    {
        if (value == Guid.Empty)
            throw new ArgumentException("Peer ID cannot be empty.", nameof(value));
        Value = value;
    }

    public static ConversationId NewId() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString();
}