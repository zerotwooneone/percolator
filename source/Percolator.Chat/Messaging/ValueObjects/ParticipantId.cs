namespace Percolator.Chat.Messaging.ValueObjects;

[Obsolete("Delete this and use ChatPeerId instead.")]
public readonly record struct ParticipantId(uint Value)
{
    public override string ToString() => Value.ToString();
}
