namespace Percolator.Cryptography;

public readonly record struct ProtocolVersion
{
    public int Value { get; }
    public ProtocolVersion(int value)
    {
        if (value <= 0) throw new ArgumentOutOfRangeException(nameof(value));
        Value = value;
    }
    public override string ToString() => Value.ToString();
}
