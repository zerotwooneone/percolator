namespace Percolator.Chat.ValueObjects;

/// <summary>
/// Monotonic counter for group state revisions.
/// </summary>
public readonly record struct GroupEpoch(uint Value)
{
    public override string ToString() => Value.ToString();
}
