using System.Buffers.Binary;
using Percolator.Domain.Security.Ports;
using Percolator.SourceGenerators;

namespace Percolator.Domain.Identities.ValueObjects;

[ByteArray(length: 64)]
public sealed partial record DeviceLinkProof
{
    public const int SignablePayloadLength = 25 + 4 + 32;

    public static ReadOnlySpan<byte> DomainPrefix => "percolator-device-link-v1"u8;

    public static void BuildSignablePayload(
        DeviceId deviceId,
        IdentityKey secondaryDevicePublicKey,
        Span<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(secondaryDevicePublicKey);
        if (destination.Length < SignablePayloadLength)
        {
            throw new ArgumentException($"Destination span must be at least {SignablePayloadLength} bytes.", nameof(destination));
        }

        DomainPrefix.CopyTo(destination[..25]);
        BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(25, 4), deviceId.Value);
        secondaryDevicePublicKey.Span.CopyTo(destination.Slice(29, 32));
    }

    public static DeviceLinkProof CreateSigned(
        DeviceId deviceId,
        IdentityKey secondaryDevicePublicKey,
        ReadOnlySpan<byte> primaryPrivateKey,
        ICryptoEngine cryptoEngine)
    {
        ArgumentNullException.ThrowIfNull(secondaryDevicePublicKey);
        ArgumentNullException.ThrowIfNull(cryptoEngine);

        Span<byte> payload = stackalloc byte[SignablePayloadLength];
        BuildSignablePayload(deviceId, secondaryDevicePublicKey, payload);
        var signature = cryptoEngine.SignEd25519(primaryPrivateKey, payload);
        return FromBytesOwned(signature);
    }

    public bool Verify(
        DeviceId deviceId,
        IdentityKey secondaryDevicePublicKey,
        IdentityKey primaryPublicKey,
        ICryptoEngine cryptoEngine)
    {
        ArgumentNullException.ThrowIfNull(secondaryDevicePublicKey);
        ArgumentNullException.ThrowIfNull(primaryPublicKey);
        ArgumentNullException.ThrowIfNull(cryptoEngine);

        Span<byte> payload = stackalloc byte[SignablePayloadLength];
        BuildSignablePayload(deviceId, secondaryDevicePublicKey, payload);
        return cryptoEngine.VerifyEd25519Signature(primaryPublicKey, payload, Span);
    }
}
