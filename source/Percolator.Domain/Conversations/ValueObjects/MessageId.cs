namespace Percolator.Domain.Conversations.ValueObjects;

public readonly record struct MessageId(Guid Value) : IEquatable<MessageId>
{
    public static MessageId New() => new(Guid.NewGuid());

    public bool IsEmpty => Value == Guid.Empty;

    public override string ToString() => Value.ToString();
}
