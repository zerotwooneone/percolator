using System.Buffers.Binary;
using Percolator.Domain.Common;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Application2.Ingress;

/// <summary>
/// Double Ratchet wire header structure:
/// [0..31]:  DhPublicKey (32 bytes)
/// [32..35]: MessageCounter (4 bytes, Big-Endian uint)
/// [36..39]: PreviousChainLength (4 bytes, Big-Endian uint)
/// Total header length: 40 bytes.
/// </summary>
public readonly record struct RatchetWireFrame(
    DhPublicKey DhPublicKey,
    uint MessageCounter,
    uint PreviousChainLength)
{
    public const int HeaderSize = 40;

    public void WriteTo(Span<byte> destination)
    {
        if (destination.Length < HeaderSize)
        {
            throw new ArgumentException("Destination buffer too small for RatchetWireFrame header.", nameof(destination));
        }

        DhPublicKey.Span.CopyTo(destination[..32]);
        BinaryPrimitives.WriteUInt32BigEndian(destination[32..36], MessageCounter);
        BinaryPrimitives.WriteUInt32BigEndian(destination[36..40], PreviousChainLength);
    }

    public static DomainResult<RatchetWireFrame> TryParse(ReadOnlySpan<byte> span)
    {
        if (span.Length < HeaderSize)
        {
            return DomainResult<RatchetWireFrame>.Failure(new DomainError("FRAME_TOO_SHORT", "Wire frame header is smaller than 40 bytes."));
        }

        if (!DhPublicKey.TryFromSpan(span[..32], out var dhKey) || dhKey is null)
        {
            return DomainResult<RatchetWireFrame>.Failure(new DomainError("INVALID_DH_KEY", "Invalid DhPublicKey in wire frame header."));
        }

        uint counter = BinaryPrimitives.ReadUInt32BigEndian(span[32..36]);
        uint prevLength = BinaryPrimitives.ReadUInt32BigEndian(span[36..40]);

        return DomainResult<RatchetWireFrame>.Success(new RatchetWireFrame(dhKey, counter, prevLength));
    }
}
