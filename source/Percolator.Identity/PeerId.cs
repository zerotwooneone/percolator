namespace Percolator.Identity;

public readonly record struct PeerId(uint Value)
{
    public override string ToString() => Value.ToString();
}