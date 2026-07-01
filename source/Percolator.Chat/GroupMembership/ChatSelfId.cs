namespace Percolator.Chat.GroupMembership;

/// <summary>
/// Chat-native local self identifier. Represents the local user's identity within the chat domain.
/// Semantically distinct from ChatPeerId (which represents remote peers).
/// </summary>
public readonly record struct ChatSelfId(uint Value)
{
    public override string ToString() => Value.ToString();
}
