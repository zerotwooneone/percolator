namespace Percolator.Chat.Messaging.ValueObjects;

public record AckId(Guid Value)
{
    public static AckId NewId() => new(Guid.NewGuid());
    public static AckId FromBytes(byte[] bytes) => new(new Guid(bytes));

    public override string ToString() => Value.ToString();
}
