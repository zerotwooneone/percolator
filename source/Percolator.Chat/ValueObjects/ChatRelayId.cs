namespace Percolator.Chat.ValueObjects;

/// <summary>
/// Chat-native identifier for the Relay hosting a group conversation.
/// </summary>
public readonly record struct ChatRelayId(Guid Value)
{
    public override string ToString() => Value.ToString();
}
