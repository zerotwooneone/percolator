namespace Percolator.Network;

public readonly record struct DirectSessionId(System.Guid Value)
{
    public static DirectSessionId NewId() => new(System.Guid.NewGuid());
    public override string ToString() => Value.ToString();
}
