namespace Percolator.Identity;

public readonly record struct SelfId(uint Value)
{
    public override string ToString() => Value.ToString();
}
