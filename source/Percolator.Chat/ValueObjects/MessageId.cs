namespace Percolator.Chat.ValueObjects;

public readonly record struct MessageId(Guid Value)
{
    public static MessageId NewId() => new(Guid.NewGuid());
}
