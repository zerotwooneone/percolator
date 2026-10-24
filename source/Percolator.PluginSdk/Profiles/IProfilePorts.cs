using Percolator.Domain.Common;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.Ports;
using Percolator.Domain.Security.ValueObjects;

namespace Percolator.PluginSdk;

/// <summary>
/// Identifies the network source type through which a peer profile was observed.
/// </summary>
public enum ProfileSourceType
{
    DirectP2P = 1,
    Relay = 2
}

/// <summary>
/// Domain model record representing a cached decrypted profile of a remote peer.
/// </summary>
public sealed record PeerProfileRecord(
    PublicIdentityId PeerId,
    string DisplayName,
    string? StatusBio,
    BlobReference? AvatarReference,
    uint Revision,
    DateTimeOffset UpdatedAtUtc);

/// <summary>
/// Multi-source snapshot representing an observed profile from a specific source endpoint.
/// Bounded per peer without trusting any remote-provided expiration timestamps.
/// </summary>
public sealed record PeerProfileSourceSnapshot(
    PublicIdentityId PeerId,
    string SourceEndpoint,
    ProfileSourceType SourceType,
    ProfileMetadata Metadata,
    BlindedProfileLocator BlindedLocator,
    DateTimeOffset LastObservedAtUtc);

/// <summary>
/// Persistence port for storing and retrieving decrypted peer profile records and multi-source snapshots on the client.
/// </summary>
public interface IPeerProfileRepository
{
    Task SaveAsync(PeerProfileRecord record, CancellationToken ct = default);
    Task<PeerProfileRecord?> GetByPeerIdAsync(PublicIdentityId peerId, CancellationToken ct = default);

    Task SaveSourceSnapshotAsync(PeerProfileSourceSnapshot snapshot, int maxSnapshotsPerPeer = 5, CancellationToken ct = default);
    Task<IReadOnlyList<PeerProfileSourceSnapshot>> GetSourceSnapshotsAsync(PublicIdentityId peerId, CancellationToken ct = default);
}

/// <summary>
/// Cached routing capability for delivering sealed messages to a peer's relay mailbox.
/// Holds the peer's mailbox routing token and the derived sealed-sender delivery token.
/// </summary>
public sealed record PeerMailboxRoutingCapability(
    PublicIdentityId PeerId,
    BlindedRoutingToken RoutingToken,
    DeliveryToken DeliveryToken,
    RelayEndpoint? RelayEndpoint = null,
    DateTimeOffset LastObservedAtUtc = default,
    DateTimeOffset? LastSuccessAtUtc = null,
    DateTimeOffset? BackpressureCooldownUntilUtc = null)
{
    public bool IsAvailable(DateTimeOffset nowUtc) =>
        BackpressureCooldownUntilUtc == null || BackpressureCooldownUntilUtc.Value <= nowUtc;
}

/// <summary>
/// Persistence port for storing and retrieving peer mailbox routing capabilities derived from profile capabilities.
/// </summary>
public interface IPeerMailboxRoutingRepository
{
    Task SaveAsync(PeerMailboxRoutingCapability capability, CancellationToken ct = default);
    Task<PeerMailboxRoutingCapability?> GetByPeerIdAsync(PublicIdentityId peerId, CancellationToken ct = default);
    Task<IReadOnlyList<PeerMailboxRoutingCapability>> GetRoutesForPeerAsync(PublicIdentityId peerId, CancellationToken ct = default);
    Task<PeerMailboxRoutingCapability?> GetBestRouteAsync(PublicIdentityId peerId, DateTimeOffset nowUtc, CancellationToken ct = default);
    Task RecordBackpressureAsync(PublicIdentityId peerId, RelayEndpoint relayEndpoint, TimeSpan retryAfter, DateTimeOffset nowUtc, CancellationToken ct = default);
    Task RecordSuccessAsync(PublicIdentityId peerId, RelayEndpoint relayEndpoint, DateTimeOffset nowUtc, CancellationToken ct = default);
}

/// <summary>
/// Decrypted profile metadata payload prior to packing and ISO/IEC 7816-4 padding.
/// </summary>
public readonly record struct ProfileMetadata(
    string DisplayName,
    byte[]? AvatarBytes,
    string? StatusBio,
    uint Revision,
    BlobReference? AvatarReference = null);

/// <summary>
/// Infrastructure serialization port for packing profile metadata to/from compact binary or protobuf bytes.
/// Prevents the Application layer from depending on concrete wire formats or serializers.
/// </summary>
public interface IProfileMetadataSerializer
{
    byte[] Serialize(ProfileMetadata metadata);
    DomainResult<ProfileMetadata> Deserialize(ReadOnlySpan<byte> data);
}

/// <summary>
/// Encryption and decryption port for compact padded profile packages.
/// </summary>
public interface IProfileManager
{
    DomainResult<ProfileCiphertextPackage> EncryptProfile(
        ProfileMetadata metadata,
        ProfileKey profileKey,
        ICryptoEngine engine);

    DomainResult<ProfileMetadata> DecryptProfile(
        ProfileCiphertextPackage package,
        ProfileKey profileKey,
        ICryptoEngine engine,
        uint? expectedRevision = null);
}

/// <summary>
/// Delivery client port for unauthenticated profile queries and mutations against Relays or direct peer endpoints.
/// </summary>
public interface IProfileDeliveryClient
{
    ValueTask<DomainResult<ProfileCiphertextPackage>> FetchProfileAsync(
        Uri? endpointUri,
        BlindedProfileLocator locator,
        CancellationToken ct = default);

    ValueTask<DomainResult> UploadProfileAsync(
        Uri? endpointUri,
        BlindedProfileLocator locator,
        ProfileWriteToken writeToken,
        ProfileCiphertextPackage package,
        uint revision,
        DateTimeOffset expiresAtUtc,
        CancellationToken ct = default);

    ValueTask<DomainResult> DeleteProfileAsync(
        Uri? endpointUri,
        BlindedProfileLocator locator,
        ProfileWriteToken writeToken,
        CancellationToken ct = default);
}
