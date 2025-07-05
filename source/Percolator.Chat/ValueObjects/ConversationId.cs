namespace Percolator.Chat.ValueObjects;

public readonly record struct ConversationId(Guid Value)
{
    public static ConversationId NewId() => new(Guid.NewGuid());
}
