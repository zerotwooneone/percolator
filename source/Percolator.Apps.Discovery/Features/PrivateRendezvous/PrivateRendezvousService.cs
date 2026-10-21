using System.Collections.Concurrent;
using Percolator.Domain.Common;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Apps.Discovery.Features.PrivateRendezvous;

public sealed class PrivateRendezvousService : IPrivateRendezvousService
{
    public const int DefaultMaxEntries = 10_000;
    public const int MaxQueryLocators = 100;
    public static readonly TimeSpan DefaultEntryTtl = TimeSpan.FromHours(48);

    private sealed record StoredRendezvousEntry(
        BlindedLocator Locator,
        EncryptedRendezvousRoute EncryptedRoute,
        string LegacyRouteDescriptor,
        DateTimeOffset PublishedAtUtc);

    private readonly ConcurrentDictionary<BlindedLocator, StoredRendezvousEntry> _rendezvousTable = new();
    private readonly IDateTimeProvider? _clock;
    private readonly TimeSpan _entryTtl;
    private readonly int _maxEntries;

    public PrivateRendezvousService(
        IDateTimeProvider? clock = null,
        TimeSpan? entryTtl = null,
        int maxEntries = DefaultMaxEntries)
    {
        _clock = clock;
        _entryTtl = entryTtl ?? DefaultEntryTtl;
        _maxEntries = maxEntries > 0 ? maxEntries : DefaultMaxEntries;
    }

    public BlindedLocator DeriveLocator(
        PublicIdentityId peerId,
        ReadOnlySpan<byte> sharedSecret,
        DiscoveryEpoch epoch)
    {
        return BlindedLocator.Derive(peerId, sharedSecret, epoch);
    }

    public IReadOnlyList<BlindedLocator> DeriveLocatorWindow(
        PublicIdentityId peerId,
        ReadOnlySpan<byte> sharedSecret,
        DiscoveryEpoch currentEpoch)
    {
        return BlindedLocator.DeriveWindow(peerId, sharedSecret, currentEpoch);
    }

    public ValueTask<DomainResult> PublishEncryptedRendezvousAsync(
        BlindedLocator locator,
        EncryptedRendezvousRoute encryptedRoute,
        CancellationToken ct = default)
    {
        if (!locator.IsValid)
        {
            return ValueTask.FromResult(DomainResult.Failure(new DomainError(
                "INVALID_LOCATOR", "Blinded locator cannot be empty or invalid.")));
        }

        if (!encryptedRoute.IsValid)
        {
            return ValueTask.FromResult(DomainResult.Failure(new DomainError(
                "INVALID_ENCRYPTED_ROUTE", "Encrypted rendezvous route is invalid.")));
        }

        DateTimeOffset nowUtc = _clock?.UtcNow ?? DateTimeOffset.UnixEpoch;
        EvictExpired(nowUtc);

        if (!_rendezvousTable.ContainsKey(locator) && _rendezvousTable.Count >= _maxEntries)
        {
            return ValueTask.FromResult(DomainResult.Failure(new DomainError(
                "RENDEZVOUS_CAPACITY_EXCEEDED", "Relay rendezvous table capacity has been reached.")));
        }

        _rendezvousTable[locator] = new StoredRendezvousEntry(
            locator,
            encryptedRoute,
            Convert.ToBase64String(encryptedRoute.Span),
            nowUtc);

        return ValueTask.FromResult(DomainResult.Success());
    }

    public ValueTask<DomainResult> PublishRendezvousAsync(
        BlindedLocator locator,
        string relayRouteDescriptor,
        CancellationToken ct = default)
    {
        if (!locator.IsValid)
        {
            return ValueTask.FromResult(DomainResult.Failure(new DomainError(
                "INVALID_LOCATOR", "Blinded locator cannot be empty.")));
        }

        var routeCheck = RelayRouteDescriptor.Create(relayRouteDescriptor);
        if (routeCheck.IsFailure)
        {
            return ValueTask.FromResult(DomainResult.Failure(routeCheck.Error));
        }

        DateTimeOffset nowUtc = _clock?.UtcNow ?? DateTimeOffset.UnixEpoch;
        EvictExpired(nowUtc);

        if (!_rendezvousTable.ContainsKey(locator) && _rendezvousTable.Count >= _maxEntries)
        {
            return ValueTask.FromResult(DomainResult.Failure(new DomainError(
                "RENDEZVOUS_CAPACITY_EXCEEDED", "Relay rendezvous table capacity has been reached.")));
        }

        _rendezvousTable[locator] = new StoredRendezvousEntry(
            locator,
            default,
            routeCheck.Value.Value,
            nowUtc);

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

        DateTimeOffset nowUtc = _clock?.UtcNow ?? DateTimeOffset.UnixEpoch;
        EvictExpired(nowUtc);

        int maxToInspect = Math.Min(locators.Count, MaxQueryLocators);
        var matches = new List<BlindedRendezvousMatch>();
        for (int i = 0; i < maxToInspect; i++)
        {
            BlindedLocator loc = locators[i];
            if (!loc.IsValid)
            {
                continue;
            }

            if (_rendezvousTable.TryGetValue(loc, out var entry))
            {
                matches.Add(new BlindedRendezvousMatch(
                    loc,
                    entry.EncryptedRoute,
                    entry.LegacyRouteDescriptor));
            }
        }

        return ValueTask.FromResult<IReadOnlyList<BlindedRendezvousMatch>>(matches);
    }

    private void EvictExpired(DateTimeOffset nowUtc)
    {
        if (_clock is null || nowUtc == DateTimeOffset.UnixEpoch)
        {
            return;
        }

        foreach (var kvp in _rendezvousTable)
        {
            if (kvp.Value.PublishedAtUtc != DateTimeOffset.UnixEpoch &&
                nowUtc - kvp.Value.PublishedAtUtc > _entryTtl)
            {
                _rendezvousTable.TryRemove(kvp.Key, out _);
            }
        }
    }
}
