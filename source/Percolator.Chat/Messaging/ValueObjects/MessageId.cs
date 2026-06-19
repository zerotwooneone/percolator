namespace Percolator.Chat.Messaging.ValueObjects;

public readonly record struct MessageId(Guid Value)
{
    public static MessageId NewId() => new(Guid.NewGuid());
}
