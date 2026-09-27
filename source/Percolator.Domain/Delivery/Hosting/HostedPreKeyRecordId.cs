using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Domain.Delivery.Hosting;

public readonly record struct HostedPreKeyRecordId(PublicIdentityId OwnerId, DeviceId DeviceId)
{
    public override string ToString() => $"{OwnerId}:{DeviceId.Value}";
}
