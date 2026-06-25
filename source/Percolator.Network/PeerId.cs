namespace Percolator.Network;

public readonly record struct PeerId(uint Value)
{
    public override string ToString() => Value.ToString();
}
