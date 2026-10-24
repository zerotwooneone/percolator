using Percolator.Domain.Common;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.Ports;
using Percolator.Domain.Security.ValueObjects;

namespace Percolator.PluginSdk;

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
/// Persistence port for storing and retrieving decrypted peer profile records on the client.
/// </summary>
public interface IPeerProfileRepository
{
    Task SaveAsync(PeerProfileRecord record, CancellationToken ct = default);
    Task<PeerProfileRecord?> GetByPeerIdAsync(PublicIdentityId peerId, CancellationToken ct = default);
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
