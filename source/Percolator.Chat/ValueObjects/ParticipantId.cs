namespace Percolator.Chat.ValueObjects;

public readonly record struct ParticipantId(Guid Value)
{
    public static ParticipantId NewId() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString();
}
