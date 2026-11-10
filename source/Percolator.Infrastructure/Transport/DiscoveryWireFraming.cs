namespace Percolator.Infrastructure.Transport;

using System;
using System.Buffers.Binary;
using Percolator.Domain.Identities.ValueObjects;

/// <summary>
/// Defines canonical wire formats and payload serializers for peer discovery and visibility exchange.
/// </summary>
public static class DiscoveryWireFraming
{
    public const int VisibilityPayloadLength = 28;

    /// <summary>
    /// Builds the 28-byte canonical payload for visibility registration signatures:
    /// [PeerId (16B) || Visibility (4B LE) || TimestampUnixMs (8B LE)]
    /// </summary>
    public static byte[] BuildVisibilitySignaturePayload(PublicIdentityId peerId, int visibility, long timestampUnixMs)
    {
        byte[] payload = new byte[VisibilityPayloadLength];
        peerId.ToByteArray().CopyTo(payload, 0);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(16, 4), visibility);
        BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(20, 8), timestampUnixMs);
        return payload;
    }
}
