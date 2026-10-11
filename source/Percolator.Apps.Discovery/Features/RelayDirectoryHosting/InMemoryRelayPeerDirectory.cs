using System.Collections.Concurrent;
using Percolator.Domain.Common;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Apps.Discovery.Features.RelayDirectoryHosting;

public sealed class InMemoryRelayPeerDirectory : IRelayPeerDirectory
{
    private readonly ConcurrentDictionary<PublicIdentityId, RelayPeerRegistration> _registrations = new();
    private readonly IDiscoveryAdmissionGate _admissionGate;

    public RelayDiscoveryPolicy Policy { get; set; }

    public InMemoryRelayPeerDirectory(
        IDiscoveryAdmissionGate admissionGate,
        RelayDiscoveryPolicy policy = RelayDiscoveryPolicy.Disabled)
    {
        _admissionGate = admissionGate ?? throw new ArgumentNullException(nameof(admissionGate));
        Policy = policy;
    }

    public ValueTask<DomainResult> RegisterPeerAsync(
        RelayPeerRegistration registration,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(registration);

        if (!registration.PeerId.IsValid)
        {
            return ValueTask.FromResult(DomainResult.Failure(new DomainError(
                "INVALID_PEER_ID", "Peer identifier must be valid and non-empty.")));
        }

        _registrations[registration.PeerId] = registration;
        return ValueTask.FromResult(DomainResult.Success());
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

        if (!_registrations.TryGetValue(peerId, out var existing))
        {
            return ValueTask.FromResult(DomainResult.Failure(new DomainError(
                "PEER_NOT_REGISTERED", $"Peer '{peerId}' has not registered with this directory.")));
        }

        var updated = existing with { Visibility = visibility };
        _registrations[peerId] = updated;

        return ValueTask.FromResult(DomainResult.Success());
    }

    public ValueTask<RelayPeerRegistration?> GetPeerAsync(
        PublicIdentityId peerId,
        CancellationToken ct = default)
    {
        _registrations.TryGetValue(peerId, out var registration);
        return ValueTask.FromResult<RelayPeerRegistration?>(registration);
    }

    public ValueTask<IReadOnlyList<RelayPeerRegistration>> GetDiscoverablePeersAsync(
        CancellationToken ct = default)
    {
        if (Policy == RelayDiscoveryPolicy.Disabled)
        {
            return ValueTask.FromResult<IReadOnlyList<RelayPeerRegistration>>(Array.Empty<RelayPeerRegistration>());
        }

        var results = new List<RelayPeerRegistration>();
        foreach (var reg in _registrations.Values)
        {
            var admission = _admissionGate.EvaluateAdmission(
                reg.PeerId,
                Policy,
                reg.Visibility,
                reg.HasAuthenticatedHandshake,
                reg.HasActiveSignedPreKey);

            if (admission.IsSuccess)
            {
                results.Add(reg);
            }
        }

        return ValueTask.FromResult<IReadOnlyList<RelayPeerRegistration>>(results);
    }
}
