using System.Collections.Concurrent;
using Percolator.Domain.Common;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Apps.Discovery.Features.PrivateRendezvous;

public sealed class PrivateRendezvousService : IPrivateRendezvousService
{
    private readonly ConcurrentDictionary<string, string> _rendezvousTable = new(StringComparer.OrdinalIgnoreCase);

    public BlindedLocator DeriveLocator(
        PublicIdentityId peerId,
        ReadOnlySpan<byte> sharedSecret,
        DiscoveryEpoch epoch)
    {
        return BlindedLocator.Derive(peerId, sharedSecret, epoch);
    }

    public ValueTask<DomainResult> PublishRendezvousAsync(
        BlindedLocator locator,
        string relayRouteDescriptor,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(locator.Value))
        {
            return ValueTask.FromResult(DomainResult.Failure(new DomainError(
                "INVALID_LOCATOR", "Blinded locator cannot be empty.")));
        }

        if (string.IsNullOrWhiteSpace(relayRouteDescriptor))
        {
            return ValueTask.FromResult(DomainResult.Failure(new DomainError(
                "INVALID_ROUTE", "Relay route descriptor cannot be empty.")));
        }

        _rendezvousTable[locator.Value] = relayRouteDescriptor;
        return ValueTask.FromResult(DomainResult.Success());
    }

    public ValueTask<IReadOnlyList<BlindedRendezvousMatch>> ResolveRendezvousAsync(
        IReadOnlyList<BlindedLocator> locators,
        CancellationToken ct = default)
    {
        if (locators == null || locators.Count == 0)
        {
            return ValueTask.FromResult<IReadOnlyList<BlindedRendezvousMatch>>(Array.Empty<BlindedRendezvousMatch>());
        }

        var matches = new List<BlindedRendezvousMatch>();
        foreach (var loc in locators)
        {
            if (_rendezvousTable.TryGetValue(loc.Value, out var route))
            {
                matches.Add(new BlindedRendezvousMatch(loc, route));
            }
        }

        return ValueTask.FromResult<IReadOnlyList<BlindedRendezvousMatch>>(matches);
    }
}
