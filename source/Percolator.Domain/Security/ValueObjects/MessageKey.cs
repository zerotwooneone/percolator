using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Percolator.Domain.Common;
using Percolator.SourceGenerators;

namespace Percolator.Domain.Security.ValueObjects;

[ByteArray(length: 32)]
public sealed partial record MessageKey : ISensitiveSecret
{
    public const int NonceLength = AeadNonce.NonceLength;

    /// <summary>
    /// Signal-style deterministic HKDF-SHA256 expansion of this single-use 32-byte <see cref="MessageKey"/> seed
    /// into a disjoint 32-byte AES-256 cipher key and 12-byte AES-GCM nonce.
    /// </summary>
    public ExpandedMessageKeys Expand(uint counterOrIteration = 0)
    {
        var material = new byte[AeadNonce.ExpandedMaterialLength];
        AeadNonce.ExpandMessageKeyMaterial(Span, counterOrIteration, material);
        return new ExpandedMessageKeys(material);
    }

    /// <summary>
    /// Derives the deterministic 12-byte <see cref="AeadNonce"/> for this single-use message key
    /// bound to the ratchet message counter or group sender key iteration.
    /// </summary>
    public AeadNonce DeriveNonce(uint counterOrIteration = 0)
        => AeadNonce.DeriveFromMessageKey(this, counterOrIteration);

    /// <summary>
    /// Writes the deterministic 12-byte AES-GCM nonce for this single-use message key into <paramref name="destination"/>.
    /// </summary>
    public void WriteNonce(uint counterOrIteration, Span<byte> destination)
        => AeadNonce.WriteFromMessageKey(Span, counterOrIteration, destination);

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
