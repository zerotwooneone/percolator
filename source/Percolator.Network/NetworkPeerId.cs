namespace Percolator.Network;

public readonly record struct NetworkPeerId(uint Value)
{
    public override string ToString() => Value.ToString();
}
