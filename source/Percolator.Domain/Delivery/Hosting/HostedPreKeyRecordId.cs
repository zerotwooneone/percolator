using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Domain.Delivery.Hosting;

public readonly record struct HostedPreKeyRecordId(BlindedRoutingToken RoutingToken, DeviceId DeviceId)
{
    public override string ToString() => $"{RoutingToken}:{DeviceId.Value}";
}
