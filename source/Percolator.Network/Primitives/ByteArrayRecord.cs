namespace Percolator.Network.Primitives;

public abstract record ByteArrayRecord(byte[] Value) : IEquatable<ByteArrayRecord>
{
    public virtual bool Equals(ByteArrayRecord? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        return Value.SequenceEqual(other.Value);
    }

    public override int GetHashCode()
    {
        unchecked
        {
            if (Value is null) return 0;
            var hash = 17;
            foreach (var b in Value)
            {
                hash = hash * 23 + b;
            }
            return hash;
        }
    }
}
