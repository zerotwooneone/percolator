using Percolator.Dht.Primitives;
using System.Numerics;

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

    public static BigInteger XorDistance(NodeId id1, NodeId id2)
    {
        if (id1.Value.Length != id2.Value.Length)
        {
            throw new ArgumentException("Node IDs must have the same length for XOR distance calculation.");
        }

        var xorResult = new byte[id1.Value.Length];
        for (int i = 0; i < id1.Value.Length; i++)
        {
            xorResult[i] = (byte)(id1.Value[i] ^ id2.Value[i]);
        }

        // The 'true' argument ensures the BigInteger is treated as unsigned.
        return new BigInteger(xorResult, isUnsigned: true, isBigEndian: true);
    }
}
