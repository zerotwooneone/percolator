namespace Percolator.Cryptography.Primitives;

public readonly record struct ConversationId
{
    public Guid Value { get; }

    public ConversationId(Guid value)
    {
        if (value == Guid.Empty)
            throw new ArgumentException("Conversation ID cannot be empty.", nameof(value));
        Value = value;
    }

    public static ConversationId NewId() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString();
}
