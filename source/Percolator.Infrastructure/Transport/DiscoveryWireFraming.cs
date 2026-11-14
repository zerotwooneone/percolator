namespace Percolator.Infrastructure.Transport;

using System;
using System.Buffers.Binary;
using Percolator.Domain.Identities.ValueObjects;

/// <summary>
/// Defines canonical wire formats and payload serializers for peer discovery and visibility exchange.
/// </summary>
public static class DiscoveryWireFraming
{
    /// <summary>Fixed prefix: PeerId (16B) || Visibility (4B LE) || TimestampUnixMs (8B LE).</summary>
    public const int VisibilityPayloadPrefixLength = 28;
    public const int RoutingTokenLength = 16;
    public const int DeliveryTokenLength = 32;

    /// <summary>
    /// Builds the canonical payload for visibility registration signatures:
    /// [PeerId (16B) || Visibility (4B LE) || TimestampUnixMs (8B LE)
    ///  || RoutingToken (16B) || DeliveryToken (32B)]
    /// The advertised mailbox capability tokens are bound into the signature so a
    /// relay cannot swap which mailbox a discoverable peer points at. When the peer
    /// registers Hidden (or omits tokens) the token region is all zeroes.
    /// </summary>
    public static byte[] BuildVisibilitySignaturePayload(
        Domain.Identities.PublicIdentityId peerId,
        int visibility,
        long timestampUnixMs,
        ReadOnlySpan<byte> routingToken = default,
        ReadOnlySpan<byte> deliveryToken = default)
    {
        if (!routingToken.IsEmpty && routingToken.Length != RoutingTokenLength)
        {
            throw new ArgumentException($"Routing token must be {RoutingTokenLength} bytes.", nameof(routingToken));
        }
        if (!deliveryToken.IsEmpty && deliveryToken.Length != DeliveryTokenLength)
        {
            throw new ArgumentException($"Delivery token must be {DeliveryTokenLength} bytes.", nameof(deliveryToken));
        }

        byte[] payload = new byte[VisibilityPayloadPrefixLength + RoutingTokenLength + DeliveryTokenLength];
        peerId.ToByteArray().CopyTo(payload, 0);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(16, 4), visibility);
        BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(20, 8), timestampUnixMs);
        if (!routingToken.IsEmpty)
        {
            routingToken.CopyTo(payload.AsSpan(VisibilityPayloadPrefixLength, RoutingTokenLength));
        }
        if (!deliveryToken.IsEmpty)
        {
            deliveryToken.CopyTo(payload.AsSpan(VisibilityPayloadPrefixLength + RoutingTokenLength, DeliveryTokenLength));
        }
        return payload;
    }
}
