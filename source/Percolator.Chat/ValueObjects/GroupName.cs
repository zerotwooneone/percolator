namespace Percolator.Chat.ValueObjects;

/// <summary>
/// Human-readable name for a group conversation.
/// </summary>
public readonly record struct GroupName(string Value)
{
    public override string ToString() => Value;
}
