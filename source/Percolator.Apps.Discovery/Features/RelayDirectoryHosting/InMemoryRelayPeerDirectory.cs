using System.Collections.Concurrent;
using Percolator.Apps.Discovery.Ports;
using Percolator.Domain.Common;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Apps.Discovery.Features.RelayDirectoryHosting;

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
            hasActiveSpk = await _preKeyAvailabilityPort.HasActiveSignedPreKeyAsync(registration.PeerId, ct).ConfigureAwait(false);
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

        var updated = existing.UpdateVisibility(visibility, nowUtc == DateTimeOffset.UnixEpoch ? existing.LastUpdatedAtUtc : nowUtc);
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
        CancellationToken ct = default)
    {
        if (Policy != RelayDiscoveryPolicy.Open)
        {
            return Array.Empty<RelayPeerRegistration>();
        }

        DateTimeOffset nowUtc = _clock?.UtcNow ?? DateTimeOffset.UnixEpoch;
        EvictExpired(nowUtc);

        var results = new List<RelayPeerRegistration>();
        foreach (var reg in _registrations.Values)
        {
            bool hasActiveSpk = reg.HasActiveSignedPreKey;
            if (_preKeyAvailabilityPort is not null)
            {
                hasActiveSpk = await _preKeyAvailabilityPort.HasActiveSignedPreKeyAsync(reg.PeerId, ct).ConfigureAwait(false);
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
