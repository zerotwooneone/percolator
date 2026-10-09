using Percolator.Domain.Delivery.ValueObjects;

namespace Percolator.Application2.Delivery;

public enum DeliveryRouteType
{
    DirectP2P = 1,
    RelayedOneToOne = 2,
    RelayedGroup = 3
}

public readonly record struct DeliveryRoute(
    DeliveryRouteType Type,
    Uri? DirectEndpoint,
    BlindedRoutingToken? TargetToken);
