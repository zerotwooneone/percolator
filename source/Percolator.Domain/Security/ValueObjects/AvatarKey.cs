using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Percolator.Domain.Common;
using Percolator.Domain.Security.Ports;
using Percolator.SourceGenerators;

namespace Percolator.Domain.Security.ValueObjects;

/// <summary>
/// 32-byte symmetric key derived from a ProfileKey via HKDF-SHA256 for encrypting out-of-band avatar media blobs.
/// </summary>
[ByteArray(length: 32)]
public sealed partial record AvatarKey : ISensitiveSecret
{
    public static AvatarKey Derive(ProfileKey profileKey, ICryptoEngine engine)
    {
        ArgumentNullException.ThrowIfNull(profileKey);
        ArgumentNullException.ThrowIfNull(engine);

        var bytes = engine.HkdfSha256(
            profileKey.Span,
            ReadOnlySpan<byte>.Empty,
            "PercolatorAvatarKeyV1"u8,
            32);

        return FromSpan(bytes);
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
