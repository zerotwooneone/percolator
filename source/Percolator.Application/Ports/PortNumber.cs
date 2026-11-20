namespace Percolator.Application.Ports;

public readonly record struct PortNumber(int Value)
{
    public static implicit operator int(PortNumber p) => p.Value;
    public static explicit operator PortNumber(int p) => new(p);
    public override string ToString() => Value.ToString();
}
