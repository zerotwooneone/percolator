namespace Percolator.Network.ValueObjects;

/// <summary>
/// Monotonic counter for Relay group state revisions on the server-side ledger.
/// </summary>
public readonly record struct RelayGroupEpoch(uint Value)
{
    public override string ToString() => Value.ToString();
}
