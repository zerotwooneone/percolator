using System;
using System.Buffers.Binary;

namespace Percolator.Application.Chat;

/// <summary>
/// Encapsulates the strict 40-byte wire layout for Delivery Certificate payloads.
/// Layout: [0..31] Cryptographic Fingerprint (32 bytes) | [32..39] Expiration Timestamp (8 bytes, Big-Endian)
/// </summary>
public static class DeliveryCertificateWireFormatter
{
    public const int WirePayloadLength = 40;
    private const int FingerprintLength = 32;

    public static byte[] Pack(ReadOnlySpan<byte> fingerprint, DateTimeOffset expiration)
    {
        if (fingerprint.Length != FingerprintLength)
        {
            throw new ArgumentException($"Fingerprint must be exactly {FingerprintLength} bytes.", nameof(fingerprint));
        }

        var payload = new byte[WirePayloadLength];
        
        fingerprint.CopyTo(payload.AsSpan(0, FingerprintLength));
        BinaryPrimitives.WriteInt64BigEndian(payload.AsSpan(FingerprintLength, 8), expiration.ToUnixTimeSeconds());

        return payload;
    }

    public static DateTimeOffset ExtractExpiration(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != WirePayloadLength)
        {
            throw new ArgumentException($"Invalid certificate payload length. Expected {WirePayloadLength}, got {payload.Length}.", nameof(payload));
        }

        var expirationTimestamp = BinaryPrimitives.ReadInt64BigEndian(payload.Slice(FingerprintLength, 8));
        return DateTimeOffset.FromUnixTimeSeconds(expirationTimestamp);
    }
}
