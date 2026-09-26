using Percolator.Domain.Common;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Domain.Identities.Model;

public sealed class PreKeyBundleState : AggregateRoot<DeviceId>
{
    public override DeviceId Id => DeviceId;
    public PublicIdentityId OwnerIdentityId { get; }
    public DeviceId DeviceId { get; }
    public uint AvailableOneTimePreKeysCount { get; private set; }
    public DateTimeOffset SignedPreKeyCreatedAtUtc { get; private set; }

    public PreKeyBundleState(
        PublicIdentityId ownerIdentityId,
        DeviceId deviceId,
        uint availableOneTimePreKeysCount,
        DateTimeOffset signedPreKeyCreatedAtUtc)
    {
        OwnerIdentityId = ownerIdentityId;
        DeviceId = deviceId;
        AvailableOneTimePreKeysCount = availableOneTimePreKeysCount;
        SignedPreKeyCreatedAtUtc = signedPreKeyCreatedAtUtc;
    }

    public void ConsumeOneTimePreKey()
    {
        if (AvailableOneTimePreKeysCount > 0)
        {
            AvailableOneTimePreKeysCount--;
        }
    }

    public void ReplenishOneTimePreKeys(uint count)
    {
        AvailableOneTimePreKeysCount += count;
    }
}
