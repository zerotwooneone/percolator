using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Percolator.Domain.Common;
using Percolator.SourceGenerators;

namespace Percolator.Domain.Security.ValueObjects;

/// <summary>
/// Immutable 32-byte master secret for a group channel.
/// Generated via ByteArray source generator with deterministic memory zeroization.
/// </summary>
[ByteArray(length: 32)]
public sealed partial record GroupMasterKey : ISensitiveSecret
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
