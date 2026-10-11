namespace Percolator.Apps.Discovery.Features.PrivateRendezvous;

public sealed record BlindedRendezvousQueryPayload(
    IReadOnlyList<BlindedLocator> Locators);

public sealed record BlindedRendezvousResponsePayload(
    IReadOnlyList<BlindedRendezvousMatch> Matches);

public sealed record BlindedRendezvousMatch(
    BlindedLocator Locator,
    string RelayRouteDescriptor);
