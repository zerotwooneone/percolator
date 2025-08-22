using Percolator.Dht.Primitives;

namespace Percolator.Dht;

public record NodeId : ByteArrayRecord
{
    public NodeId(byte[] value) : base(value)
    {
        if (value.Length == 0)
        {
            throw new ArgumentException("Node ID cannot be empty.", nameof(value));
        }
    }
}
