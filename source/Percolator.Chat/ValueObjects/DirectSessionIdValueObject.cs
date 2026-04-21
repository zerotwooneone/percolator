namespace Percolator.Chat.ValueObjects;

public readonly record struct DirectSessionIdValueObject(Guid Value)
{
    public static DirectSessionIdValueObject? FromGuid(Guid? guid) =>
        guid.HasValue ? new DirectSessionIdValueObject(guid.Value) : null;

    public override string ToString() => Value.ToString();
}
