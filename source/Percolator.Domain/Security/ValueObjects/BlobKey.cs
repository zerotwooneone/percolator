using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Percolator.Domain.Common;
using Percolator.SourceGenerators;

namespace Percolator.Domain.Security.ValueObjects;

/// <summary>
/// 32-byte symmetric key derived from a group master key for encrypting group blob states.
/// Generated via ByteArray source generator with deterministic memory zeroization.
/// </summary>
[ByteArray(length: 32)]
public sealed partial record BlobKey : ISensitiveSecret
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
