using Percolator.Domain.Common;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Domain.Identities.Model;

public sealed class DeviceRecord : IEntity<DeviceId>
{
    public DeviceId Id { get; }
    public string DeviceName { get; private set; }
    public IdentityPublicKey DevicePublicKey { get; private set; }
    public DeviceLinkProof? LinkProof { get; private set; }
    public DateTimeOffset RegisteredAtUtc { get; private set; }

    private DeviceRecord(
        DeviceId id,
        string deviceName,
        IdentityPublicKey devicePublicKey,
        DeviceLinkProof? linkProof,
        DateTimeOffset registeredAtUtc)
    {
        Id = id;
        DeviceName = deviceName;
        DevicePublicKey = devicePublicKey;
        LinkProof = linkProof;
        RegisteredAtUtc = registeredAtUtc;
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
        string deviceName,
        IDateTimeProvider timeProvider)
    {
        if (deviceId.IsPrimary)
        {
            return DomainResult<DeviceRecord>.Failure(new DomainError("INVALID_DEVICE_ID", "Secondary device cannot have Primary DeviceId(1)."));
        }

        if (devicePublicKey == null)
        {
            return DomainResult<DeviceRecord>.Failure(new DomainError("NULL_PUBLIC_KEY", "Device public key cannot be null."));
        }

        if (linkProof == null)
        {
            return DomainResult<DeviceRecord>.Failure(new DomainError("MISSING_LINK_PROOF", "Secondary device requires a valid DeviceLinkProof."));
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
