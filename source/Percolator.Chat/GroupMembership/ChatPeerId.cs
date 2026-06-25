namespace Percolator.Chat.GroupMembership;

/// <summary>
/// Chat-native peer identifier. Maps to Percolator.Identity.PeerId at the Application layer boundary.
/// </summary>
public readonly record struct ChatPeerId(uint Value)
{
    public override string ToString() => Value.ToString();
}
