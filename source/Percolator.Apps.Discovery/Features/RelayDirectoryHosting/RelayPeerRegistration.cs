using Percolator.Domain.Common;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Apps.Discovery.Features.RelayDirectoryHosting;

/// <summary>
/// Domain entity stored on the hosting relay/node tracking a peer's discoverability state and lifecycle.
/// </summary>
public sealed record RelayPeerRegistration
{
    public PublicIdentityId PeerId { get; init; }
    public PeerDiscoveryVisibility Visibility { get; init; }
    public bool HasAuthenticatedHandshake { get; init; }
    public bool HasActiveSignedPreKey { get; init; }
    public DateTimeOffset RegisteredAtUtc { get; init; }
    public DateTimeOffset LastUpdatedAtUtc { get; init; }

    public RelayPeerRegistration(
        PublicIdentityId PeerId,
        PeerDiscoveryVisibility Visibility,
        bool HasAuthenticatedHandshake,
        bool HasActiveSignedPreKey,
        DateTimeOffset RegisteredAtUtc = default,
        DateTimeOffset LastUpdatedAtUtc = default)
    {
        this.PeerId = PeerId;
        this.Visibility = Visibility;
        this.HasAuthenticatedHandshake = HasAuthenticatedHandshake;
        this.HasActiveSignedPreKey = HasActiveSignedPreKey;
        this.RegisteredAtUtc = RegisteredAtUtc == default ? DateTimeOffset.UnixEpoch : RegisteredAtUtc;
        this.LastUpdatedAtUtc = LastUpdatedAtUtc == default ? this.RegisteredAtUtc : LastUpdatedAtUtc;
    }

    public static DomainResult<RelayPeerRegistration> Create(
        PublicIdentityId peerId,
        PeerDiscoveryVisibility visibility,
        bool hasAuthenticatedHandshake,
        bool hasActiveSignedPreKey,
        DateTimeOffset nowUtc)
    {
        if (!peerId.IsValid)
        {
            return DomainResult<RelayPeerRegistration>.Failure(new DomainError(
                "INVALID_PEER_ID", "Peer identifier must be valid and non-empty."));
        }

        if (!hasAuthenticatedHandshake)
        {
            return DomainResult<RelayPeerRegistration>.Failure(new DomainError(
                "HANDSHAKE_MISSING", "Cannot register a peer without an authenticated handshake."));
        }

        return DomainResult<RelayPeerRegistration>.Success(new RelayPeerRegistration(
            peerId,
            visibility,
            hasAuthenticatedHandshake,
            hasActiveSignedPreKey,
            nowUtc,
            nowUtc));
    }

    public RelayPeerRegistration UpdateVisibility(PeerDiscoveryVisibility visibility, DateTimeOffset nowUtc) =>
        this with
        {
            Visibility = visibility,
            LastUpdatedAtUtc = nowUtc
        };

    public RelayPeerRegistration RefreshPreKeyStatus(bool hasActiveSignedPreKey, DateTimeOffset nowUtc) =>
        this with
        {
            HasActiveSignedPreKey = hasActiveSignedPreKey,
            LastUpdatedAtUtc = nowUtc
        };

    public bool IsExpired(DateTimeOffset nowUtc, TimeSpan ttl) =>
        ttl > TimeSpan.Zero &&
        LastUpdatedAtUtc != DateTimeOffset.UnixEpoch &&
        nowUtc - LastUpdatedAtUtc > ttl;
}
