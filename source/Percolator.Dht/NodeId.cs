using Percolator.SourceGenerators;
using System.Numerics;

namespace Percolator.Dht;

[ByteArray(length: 32)]
public sealed partial record NodeId
{

    public static BigInteger XorDistance(NodeId id1, NodeId id2)
    {
        var span1 = id1.Span;
        var span2 = id2.Span;

        var xorResult = new byte[span1.Length];
        for (int i = 0; i < span1.Length; i++)
        {
            xorResult[i] = (byte)(span1[i] ^ span2[i]);
        }

        // The 'true' argument ensures the BigInteger is treated as unsigned.
        return new BigInteger(xorResult, isUnsigned: true, isBigEndian: true);
    }
}
