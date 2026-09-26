using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Percolator.Domain.Common;
using Percolator.SourceGenerators;

namespace Percolator.Domain.Security.ValueObjects;

[ByteArray(length: 32)]
public sealed partial record ChainKey : ISensitiveSecret
{
    public void Zeroize()
    {
        if (MemoryMarshal.TryGetArray(_value, out var segment) && segment.Array != null)
        {
            CryptographicOperations.ZeroMemory(segment.Array.AsSpan(segment.Offset, segment.Count));
        }
    }

    public void Dispose()
    {
        Zeroize();
    }
}
