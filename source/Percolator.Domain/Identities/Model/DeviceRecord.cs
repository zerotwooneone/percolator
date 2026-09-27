using System.Buffers.Binary;
using Percolator.Domain.Common;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.Ports;

namespace Percolator.Domain.Identities.Model;

public sealed class DeviceRecord : IEntity<DeviceId>
{
    public DeviceId Id => DeviceId;
    public DeviceId DeviceId { get; }
    public string DeviceName { get; private set; }
    public IdentityPublicKey DevicePublicKey { get; }
    public DeviceLinkProof? LinkProof { get; }
    public DateTimeOffset CreatedAtUtc { get; }
    public DateTimeOffset? LastSeenAtUtc { get; private set; }

    private DeviceRecord(
        DeviceId deviceId,
        string deviceName,
        IdentityPublicKey devicePublicKey,
        DeviceLinkProof? linkProof,
        DateTimeOffset createdAtUtc)
    {
        DeviceId = deviceId;
        DeviceName = deviceName;
        DevicePublicKey = devicePublicKey;
        LinkProof = linkProof;
        CreatedAtUtc = createdAtUtc;
    }

    public static DomainResult<DeviceRecord> CreatePrimary(
        IdentityPublicKey devicePublicKey,
        string deviceName,
        IDateTimeProvider timeProvider)
    {
        if (devicePublicKey == null)
        {
            return DomainResult<DeviceRecord>.Failure(new DomainError("NULL_PUBLIC_KEY", "Device public key cannot be null."));
        }

        var record = new DeviceRecord(
            DeviceId.Primary,
            deviceName?.Trim() ?? "Primary Device",
            devicePublicKey,
            linkProof: null,
            timeProvider.UtcNow);

        return DomainResult<DeviceRecord>.Success(record);
    }

    public static DomainResult<DeviceRecord> CreateSecondary(
        DeviceId deviceId,
        IdentityPublicKey devicePublicKey,
        DeviceLinkProof linkProof,
        IdentityPublicKey primaryIdentityPublicKey,
        ICryptoEngine cryptoEngine,
        string deviceName,
        IDateTimeProvider timeProvider)
    {
        if (deviceId.IsPrimary || !deviceId.IsValid)
        {
            return DomainResult<DeviceRecord>.Failure(new DomainError("INVALID_DEVICE_ID", "Secondary device must have a valid non-zero DeviceId and cannot be Primary DeviceId(1)."));
        }

        if (devicePublicKey == null)
        {
            return DomainResult<DeviceRecord>.Failure(new DomainError("NULL_PUBLIC_KEY", "Device public key cannot be null."));
        }

        if (linkProof == null)
        {
            return DomainResult<DeviceRecord>.Failure(new DomainError("MISSING_LINK_PROOF", "Secondary device requires a valid DeviceLinkProof."));
        }

        if (primaryIdentityPublicKey == null)
        {
            return DomainResult<DeviceRecord>.Failure(new DomainError("NULL_PRIMARY_KEY", "Primary identity public key is required to verify link proof."));
        }

        Span<byte> messageToVerify = stackalloc byte[4 + 32];
        BinaryPrimitives.WriteUInt32LittleEndian(messageToVerify[..4], deviceId.Value);
        devicePublicKey.Span.CopyTo(messageToVerify[4..]);

        if (!cryptoEngine.VerifyEd25519Signature(primaryIdentityPublicKey, messageToVerify, linkProof.Span))
        {
            return DomainResult<DeviceRecord>.Failure(new DomainError("INVALID_LINK_PROOF_SIGNATURE", "The device link proof signature is invalid or forged."));
        }

        var record = new DeviceRecord(
            deviceId,
            deviceName?.Trim() ?? $"Device {deviceId.Value}",
            devicePublicKey,
            linkProof,
            timeProvider.UtcNow);

        return DomainResult<DeviceRecord>.Success(record);
    }
}
