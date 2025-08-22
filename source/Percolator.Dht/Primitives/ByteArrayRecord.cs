namespace Percolator.Dht.Primitives;

public abstract record ByteArrayRecord
{
    public byte[] Value { get; }

    protected ByteArrayRecord(byte[] value)
    {
        Value = value;
    }

    public override string ToString()
    {
        return Convert.ToBase64String(Value);
    }
}
