namespace Percolator.Chat.Messaging.ValueObjects;

public readonly record struct ParticipantId(uint Value)
{
    public override string ToString() => Value.ToString();
}
