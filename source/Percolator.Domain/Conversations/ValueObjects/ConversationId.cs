namespace Percolator.Domain.Conversations.ValueObjects;

public readonly record struct ConversationId(Guid Value) : IEquatable<ConversationId>
{
    public static ConversationId New() => new(Guid.NewGuid());

    public bool IsEmpty => Value == Guid.Empty;

    public override string ToString() => Value.ToString();
}
