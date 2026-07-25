namespace Percolator.Network.ValueObjects;

/// <summary>
/// Unique identifier for a Relay group on the server-side ledger.
/// </summary>
public readonly record struct RelayGroupId(Guid Value)
{
    public override string ToString() => Value.ToString();
}
