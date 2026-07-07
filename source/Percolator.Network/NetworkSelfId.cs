namespace Percolator.Network;

public readonly record struct NetworkSelfId(uint Value)
{
    public override string ToString() => Value.ToString();
}
