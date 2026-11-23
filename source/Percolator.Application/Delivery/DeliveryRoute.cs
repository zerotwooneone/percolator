namespace Percolator.Application.Delivery;

using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Delivery.ValueObjects;

public enum DeliveryRouteType
{
    RelayedOneToOne = 2,
    RelayedGroup = 3
}

public readonly record struct DeliveryRoute(
    DeliveryRouteType Type,
    BlindedRoutingToken TargetToken,
    RelayEndpoint RelayEndpoint,
    DeliveryToken? TargetDeliveryToken = null)
{
    public static DeliveryRoute RelayedOneToOne(
        BlindedRoutingToken targetToken,
        RelayEndpoint relayEndpoint,
        DeliveryToken? targetDeliveryToken = null) =>
        new(DeliveryRouteType.RelayedOneToOne, targetToken, relayEndpoint, targetDeliveryToken);

    public static DeliveryRoute RelayedGroup(
        ChannelId groupChannelId,
        RelayEndpoint relayEndpoint) =>
        new(DeliveryRouteType.RelayedGroup, BlindedRoutingToken.FromGuid(groupChannelId.Value), relayEndpoint);
}
