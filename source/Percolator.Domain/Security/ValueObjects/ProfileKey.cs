using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Percolator.Domain.Common;
using Percolator.SourceGenerators;

namespace Percolator.Domain.Security.ValueObjects;

/// <summary>
/// 32-byte symmetric key (AES-256) acting as the root authorization capability for a user's encrypted profile.
/// Holding this key allows decrypting the owner's profile metadata and deriving the owner's BlindedProfileLocator,
/// ProfileWriteToken, DeliveryToken, and AvatarKey.
/// </summary>
[ByteArray(length: 32)]
public sealed partial record ProfileKey : ISensitiveSecret
{
    public static ProfileKey Generate()
    {
        var bytes = new byte[32];
        RandomNumberGenerator.Fill(bytes);
        return FromBytesOwned(bytes);
    }

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
