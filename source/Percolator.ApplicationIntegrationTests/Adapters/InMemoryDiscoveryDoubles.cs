using System.Collections.Concurrent;
using Percolator.Apps.Discovery.Features.PrivateRendezvous;
using Percolator.Apps.Discovery.Features.RelayDirectoryHosting;
using Percolator.Apps.Discovery.Ports;
using Percolator.Domain.Common;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Application2IntegrationTests.Adapters;

/// <summary>
/// Test-assembly-local in-memory <see cref="IRelayPeerDirectory"/> double.
/// Moved out of the production Discovery assembly in Phase 5.
/// </summary>
public sealed class InMemoryRelayPeerDirectory : IRelayPeerDirectory
{
    public static readonly TimeSpan DefaultRegistrationTtl = TimeSpan.FromHours(24);

    private readonly ConcurrentDictionary<PublicIdentityId, RelayPeerRegistration> _registrations = new();
    private readonly IDiscoveryAdmissionGate _admissionGate;
    private readonly IPreKeyAvailabilityPort? _preKeyAvailabilityPort;
    private readonly IDateTimeProvider? _clock;
    private readonly TimeSpan _registrationTtl;

    public RelayDiscoveryPolicy Policy { get; set; }

    public InMemoryRelayPeerDirectory(
        IDiscoveryAdmissionGate admissionGate,
        RelayDiscoveryPolicy policy = RelayDiscoveryPolicy.Disabled,
        IPreKeyAvailabilityPort? preKeyAvailabilityPort = null,
        IDateTimeProvider? clock = null,
        TimeSpan? registrationTtl = null)
    {
        _admissionGate = admissionGate ?? throw new ArgumentNullException(nameof(admissionGate));
        Policy = policy;
        _preKeyAvailabilityPort = preKeyAvailabilityPort;
        _clock = clock;
        _registrationTtl = registrationTtl ?? DefaultRegistrationTtl;
    }

    public async ValueTask<DomainResult> RegisterPeerAsync(
        RelayPeerRegistration registration,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(registration);

        if (!registration.PeerId.IsValid)
        {
            return DomainResult.Failure(new DomainError(
                "INVALID_PEER_ID", "Peer identifier must be valid and non-empty."));
        }

        if (!registration.HasAuthenticatedHandshake)
        {
            return DomainResult.Failure(new DomainError(
                "HANDSHAKE_MISSING", "Peer must have an authenticated handshake with the relay before registering."));
        }

        DateTimeOffset nowUtc = _clock?.UtcNow ?? registration.LastUpdatedAtUtc;
        EvictExpired(nowUtc);

        bool hasActiveSpk = registration.HasActiveSignedPreKey;
        if (_preKeyAvailabilityPort is not null)
        {
            hasActiveSpk = registration.RoutingToken.HasValue &&
                await _preKeyAvailabilityPort.HasActiveSignedPreKeyAsync(registration.RoutingToken.Value, ct).ConfigureAwait(false);
        }

        var normalized = registration with
        {
            HasActiveSignedPreKey = hasActiveSpk,
            RegisteredAtUtc = registration.RegisteredAtUtc == DateTimeOffset.UnixEpoch ? nowUtc : registration.RegisteredAtUtc,
            LastUpdatedAtUtc = nowUtc
        };

        _registrations[registration.PeerId] = normalized;
        return DomainResult.Success();
    }

    public ValueTask<DomainResult> UpdateVisibilityAsync(
        PublicIdentityId peerId,
        PeerDiscoveryVisibility visibility,
        BlindedRoutingToken? routingToken = null,
        DeliveryToken? deliveryToken = null,
        CancellationToken ct = default)
    {
        if (!peerId.IsValid)
        {
            return ValueTask.FromResult(DomainResult.Failure(new DomainError(
                "INVALID_PEER_ID", "Peer identifier must be valid and non-empty.")));
        }

        DateTimeOffset nowUtc = _clock?.UtcNow ?? DateTimeOffset.UnixEpoch;
        EvictExpired(nowUtc);

        if (!_registrations.TryGetValue(peerId, out var existing))
        {
            return ValueTask.FromResult(DomainResult.Failure(new DomainError(
                "PEER_NOT_REGISTERED", $"Peer '{peerId}' has not registered with this directory.")));
        }

        var updated = existing
            .UpdateVisibility(visibility, nowUtc == DateTimeOffset.UnixEpoch ? existing.LastUpdatedAtUtc : nowUtc)
            with { RoutingToken = routingToken, DeliveryToken = deliveryToken };
        _registrations[peerId] = updated;

        return ValueTask.FromResult(DomainResult.Success());
    }

    public ValueTask<RelayPeerRegistration?> GetPeerAsync(
        PublicIdentityId peerId,
        CancellationToken ct = default)
    {
        if (_clock is not null)
        {
            EvictExpired(_clock.UtcNow);
        }

        _registrations.TryGetValue(peerId, out var registration);
        return ValueTask.FromResult<RelayPeerRegistration?>(registration);
    }

    public async ValueTask<IReadOnlyList<RelayPeerRegistration>> GetDiscoverablePeersAsync(
        int? limit = null,
        CancellationToken ct = default)
    {
        if (Policy != RelayDiscoveryPolicy.Open || (limit.HasValue && limit.Value <= 0))
        {
            return Array.Empty<RelayPeerRegistration>();
        }

        DateTimeOffset nowUtc = _clock?.UtcNow ?? DateTimeOffset.UnixEpoch;
        EvictExpired(nowUtc);

        var results = new List<RelayPeerRegistration>();
        foreach (var reg in _registrations.Values)
        {
            if (limit.HasValue && results.Count >= limit.Value)
            {
                break;
            }

            bool hasActiveSpk = reg.HasActiveSignedPreKey;
            if (_preKeyAvailabilityPort is not null)
            {
                hasActiveSpk = reg.RoutingToken.HasValue &&
                    await _preKeyAvailabilityPort.HasActiveSignedPreKeyAsync(reg.RoutingToken.Value, ct).ConfigureAwait(false);
                if (hasActiveSpk != reg.HasActiveSignedPreKey)
                {
                    _registrations[reg.PeerId] = reg.RefreshPreKeyStatus(hasActiveSpk, nowUtc);
                }
            }

            var admission = _admissionGate.EvaluateAdmission(
                reg.PeerId,
                Policy,
                reg.Visibility,
                reg.HasAuthenticatedHandshake,
                hasActiveSpk);

            if (admission.IsSuccess)
            {
                results.Add(hasActiveSpk == reg.HasActiveSignedPreKey ? reg : reg.RefreshPreKeyStatus(hasActiveSpk, nowUtc));
            }
        }

        return results;
    }

    private void EvictExpired(DateTimeOffset nowUtc)
    {
        if (_clock is null || nowUtc == DateTimeOffset.UnixEpoch)
        {
            return;
        }

        foreach (var kvp in _registrations)
        {
            if (kvp.Value.IsExpired(nowUtc, _registrationTtl))
            {
                _registrations.TryRemove(kvp.Key, out _);
            }
        }
    }
}

/// <summary>
/// Test-assembly-local in-memory <see cref="IPrivateRendezvousService"/> double.
/// Moved out of the production Discovery assembly in Phase 5.
/// </summary>
public sealed class PrivateRendezvousService : IPrivateRendezvousService
{
    public const int DefaultMaxEntries = 10_000;
    public const int MaxQueryLocators = 100;
    public static readonly TimeSpan DefaultEntryTtl = TimeSpan.FromHours(48);

    private sealed record StoredRendezvousEntry(
        BlindedLocator Locator,
        EncryptedRendezvousRoute EncryptedRoute,
        RelayRouteDescriptor? RouteDescriptor,
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

    public BlindedLocator DeriveLocator(
        PublicIdentityId peerId,
        RendezvousSharedSecret sharedSecret,
        DiscoveryEpoch epoch) =>
        DeriveLocator(peerId, sharedSecret.Span, epoch);

    public IReadOnlyList<BlindedLocator> DeriveLocatorWindow(
        PublicIdentityId peerId,
        ReadOnlySpan<byte> sharedSecret,
        DiscoveryEpoch currentEpoch)
    {
        return BlindedLocator.DeriveWindow(peerId, sharedSecret, currentEpoch);
    }

    public IReadOnlyList<BlindedLocator> DeriveLocatorWindow(
        PublicIdentityId peerId,
        RendezvousSharedSecret sharedSecret,
        DiscoveryEpoch currentEpoch) =>
        DeriveLocatorWindow(peerId, sharedSecret.Span, currentEpoch);

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
            null,
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
            routeCheck.Value,
            nowUtc);

        return ValueTask.FromResult(DomainResult.Success());
    }

    public ValueTask<IReadOnlyList<BlindedRendezvousMatch>> ResolveRendezvousAsync(
        IReadOnlyList<BlindedLocator> locators,
        CancellationToken ct = default)
    {
        DateTimeOffset nowUtc = _clock?.UtcNow ?? DateTimeOffset.UnixEpoch;
        EvictExpired(nowUtc);

        if (locators == null || locators.Count == 0)
        {
            return ValueTask.FromResult<IReadOnlyList<BlindedRendezvousMatch>>(Array.Empty<BlindedRendezvousMatch>());
        }

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
                    entry.RouteDescriptor));
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
                nowUtc - kvp.Value.PublishedAtUtc >= _entryTtl)
            {
                _rendezvousTable.TryRemove(kvp.Key, out _);
            }
        }
    }
}
