namespace Percolator.Infrastructure.Persistence.Repositories;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Percolator.Apps.Discovery.Features.RelayDirectoryHosting;
using Percolator.Apps.Discovery.Ports;
using Percolator.Domain.Common;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Infrastructure.Persistence;
using Percolator.Infrastructure.Persistence.Entities;

public sealed class SqliteRelayPeerDirectory : IRelayPeerDirectory
{
    public static readonly TimeSpan DefaultRegistrationTtl = TimeSpan.FromHours(24);

    private readonly IDbContextFactory<PercolatorDbContext> _contextFactory;
    private readonly IDiscoveryAdmissionGate _admissionGate;
    private readonly IPreKeyAvailabilityPort? _preKeyAvailabilityPort;
    private readonly IDateTimeProvider? _clock;
    private readonly TimeSpan _registrationTtl;

    public RelayDiscoveryPolicy Policy { get; set; }

    public SqliteRelayPeerDirectory(
        IDbContextFactory<PercolatorDbContext> contextFactory,
        IDiscoveryAdmissionGate admissionGate,
        RelayDiscoveryPolicy policy = RelayDiscoveryPolicy.Disabled,
        IPreKeyAvailabilityPort? preKeyAvailabilityPort = null,
        IDateTimeProvider? clock = null,
        TimeSpan? registrationTtl = null)
    {
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
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

        await using var db = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        DateTimeOffset nowUtc = _clock?.UtcNow ?? (registration.LastUpdatedAtUtc == DateTimeOffset.UnixEpoch ? DateTimeOffset.UtcNow : registration.LastUpdatedAtUtc);
        byte[] peerIdBytes = registration.PeerId.ToByteArray();

        // Privacy invariant: Setting visibility to Hidden immediately removes the peer's row from relay_peer_directory
        // and succeeds without running admission gating (which is only for discoverability listing).
        if (registration.Visibility == PeerDiscoveryVisibility.Hidden)
        {
            var existingRecord = await db.RelayPeerDirectory
                .FirstOrDefaultAsync(e => e.PeerId == peerIdBytes, ct)
                .ConfigureAwait(false);

            if (existingRecord != null)
            {
                db.RelayPeerDirectory.Remove(existingRecord);
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
            }

            return DomainResult.Success();
        }

        bool hasActiveSpk = registration.HasActiveSignedPreKey;
        if (_preKeyAvailabilityPort != null)
        {
            hasActiveSpk = await _preKeyAvailabilityPort.HasActiveSignedPreKeyAsync(registration.PeerId, ct).ConfigureAwait(false);
        }

        var admission = _admissionGate.EvaluateAdmission(
            registration.PeerId,
            Policy,
            registration.Visibility,
            registration.HasAuthenticatedHandshake,
            hasActiveSpk);

        if (!admission.IsSuccess)
        {
            return admission;
        }

        var existing = await db.RelayPeerDirectory
            .FirstOrDefaultAsync(e => e.PeerId == peerIdBytes, ct)
            .ConfigureAwait(false);

        if (existing == null)
        {
            DateTimeOffset registeredAt = registration.RegisteredAtUtc == DateTimeOffset.UnixEpoch ? nowUtc : registration.RegisteredAtUtc;
            var entity = new RelayPeerDirectoryEntity
            {
                PeerId = peerIdBytes,
                Visibility = (int)registration.Visibility,
                HasAuthenticatedHandshake = true,
                HasActiveSignedPreKey = hasActiveSpk,
                RegisteredAtUtc = registeredAt,
                LastUpdatedAtUtc = nowUtc
            };

            await db.RelayPeerDirectory.AddAsync(entity, ct).ConfigureAwait(false);
        }
        else
        {
            existing.Visibility = (int)registration.Visibility;
            existing.HasAuthenticatedHandshake = true;
            existing.HasActiveSignedPreKey = hasActiveSpk;
            existing.LastUpdatedAtUtc = nowUtc;
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return DomainResult.Success();
    }

    public async ValueTask<DomainResult> UpdateVisibilityAsync(
        PublicIdentityId peerId,
        PeerDiscoveryVisibility visibility,
        CancellationToken ct = default)
    {
        if (!peerId.IsValid)
        {
            return DomainResult.Failure(new DomainError(
                "INVALID_PEER_ID", "Peer identifier must be valid and non-empty."));
        }

        await using var db = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        DateTimeOffset nowUtc = _clock?.UtcNow ?? DateTimeOffset.UtcNow;
        byte[] peerIdBytes = peerId.ToByteArray();
        var existing = await db.RelayPeerDirectory
            .FirstOrDefaultAsync(e => e.PeerId == peerIdBytes, ct)
            .ConfigureAwait(false);

        if (existing == null)
        {
            return visibility == PeerDiscoveryVisibility.Hidden
                ? DomainResult.Success()
                : DomainResult.Failure(new DomainError(
                    "PEER_NOT_REGISTERED", $"Peer '{peerId}' has not registered with this directory."));
        }

        if (visibility == PeerDiscoveryVisibility.Hidden)
        {
            db.RelayPeerDirectory.Remove(existing);
        }
        else
        {
            existing.Visibility = (int)visibility;
            existing.LastUpdatedAtUtc = nowUtc;
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return DomainResult.Success();
    }

    public async ValueTask<RelayPeerRegistration?> GetPeerAsync(
        PublicIdentityId peerId,
        CancellationToken ct = default)
    {
        if (!peerId.IsValid)
        {
            return null;
        }

        await using var db = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        DateTimeOffset nowUtc = _clock?.UtcNow ?? DateTimeOffset.UtcNow;
        byte[] peerIdBytes = peerId.ToByteArray();

        var existing = await db.RelayPeerDirectory
            .FirstOrDefaultAsync(e => e.PeerId == peerIdBytes, ct)
            .ConfigureAwait(false);

        if (existing == null)
        {
            return null;
        }

        if (nowUtc - existing.LastUpdatedAtUtc >= _registrationTtl)
        {
            db.RelayPeerDirectory.Remove(existing);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return null;
        }

        return new RelayPeerRegistration(
            peerId,
            (PeerDiscoveryVisibility)existing.Visibility,
            existing.HasAuthenticatedHandshake,
            existing.HasActiveSignedPreKey,
            existing.RegisteredAtUtc,
            existing.LastUpdatedAtUtc);
    }

    public async ValueTask<IReadOnlyList<RelayPeerRegistration>> GetDiscoverablePeersAsync(
        int? limit = null,
        CancellationToken ct = default)
    {
        if (Policy != RelayDiscoveryPolicy.Open || (limit.HasValue && limit.Value <= 0))
        {
            return Array.Empty<RelayPeerRegistration>();
        }

        await using var db = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        DateTimeOffset nowUtc = _clock?.UtcNow ?? DateTimeOffset.UtcNow;
        DateTimeOffset minUpdated = nowUtc - _registrationTtl;
        int fetchBatchSize = limit.HasValue ? Math.Clamp(limit.Value * 2, 20, 200) : 100;

        var candidates = await db.RelayPeerDirectory
            .Where(e => e.Visibility == (int)PeerDiscoveryVisibility.Discoverable && e.LastUpdatedAtUtc > minUpdated)
            .OrderBy(e => e.LastUpdatedAtUtc)
            .Take(fetchBatchSize)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var results = new List<RelayPeerRegistration>();
        bool changed = false;

        foreach (var reg in candidates)
        {
            if (limit.HasValue && results.Count >= limit.Value)
            {
                break;
            }

            var peerId = PublicIdentityId.FromBytes(reg.PeerId);
            bool hasActiveSpk = reg.HasActiveSignedPreKey;

            if (_preKeyAvailabilityPort != null)
            {
                hasActiveSpk = await _preKeyAvailabilityPort.HasActiveSignedPreKeyAsync(peerId, ct).ConfigureAwait(false);
                if (hasActiveSpk != reg.HasActiveSignedPreKey)
                {
                    reg.HasActiveSignedPreKey = hasActiveSpk;
                    changed = true;
                }
            }

            var admission = _admissionGate.EvaluateAdmission(
                peerId,
                Policy,
                (PeerDiscoveryVisibility)reg.Visibility,
                reg.HasAuthenticatedHandshake,
                hasActiveSpk);

            if (admission.IsSuccess)
            {
                results.Add(new RelayPeerRegistration(
                    peerId,
                    (PeerDiscoveryVisibility)reg.Visibility,
                    reg.HasAuthenticatedHandshake,
                    hasActiveSpk,
                    reg.RegisteredAtUtc,
                    reg.LastUpdatedAtUtc));
            }
        }

        if (changed)
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        return results;
    }
}
