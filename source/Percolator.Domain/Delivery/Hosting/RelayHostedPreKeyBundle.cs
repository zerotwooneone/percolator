using System.ComponentModel;
using Percolator.Domain.Common;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.Ports;

namespace Percolator.Domain.Delivery.Hosting;

/// <summary>
/// Aggregate root representing the pre-key bundle material hosted on a relay for a specific identity and device.
/// Scoped exclusively to a single device to guarantee sub-millisecond lookups, low memory footprint,
/// and isolated concurrency without cross-identity lock contention.
/// </summary>
public sealed class RelayHostedPreKeyBundle : AggregateRoot<HostedPreKeyRecordId>
{
    public const int MaxOneTimePreKeyCapacity = 100;
    public override HostedPreKeyRecordId Id => new(OwnerId, DeviceId);
    public PublicIdentityId OwnerId { get; }
    public DeviceId DeviceId { get; }
    public IdentityKey IdentityKey { get; }
    public DhPublicKey SignedPreKey { get; private set; }
    public DeviceLinkProof SignedPreKeySignature { get; private set; }

    private readonly Queue<(uint KeyId, DhPublicKey Key)> _oneTimePreKeys = new();
    public int AvailableOneTimePreKeyCount => _oneTimePreKeys.Count;
    public IReadOnlyCollection<(uint KeyId, DhPublicKey Key)> OneTimePreKeys => _oneTimePreKeys;

    private RelayHostedPreKeyBundle(
        PublicIdentityId ownerId,
        DeviceId deviceId,
        IdentityKey identityKey,
        DhPublicKey signedPreKey,
        DeviceLinkProof signedPreKeySignature)
    {
        OwnerId = ownerId;
        DeviceId = deviceId;
        IdentityKey = identityKey;
        SignedPreKey = signedPreKey;
        SignedPreKeySignature = signedPreKeySignature;
    }

    [EditorBrowsable(EditorBrowsableState.Never)]
    public static RelayHostedPreKeyBundle Rehydrate(
        PublicIdentityId ownerId,
        DeviceId deviceId,
        IdentityKey identityKey,
        DhPublicKey signedPreKey,
        DeviceLinkProof signedPreKeySignature,
        IEnumerable<(uint KeyId, DhPublicKey Key)>? oneTimePreKeys = null)
    {
        if (!ownerId.IsValid) throw new ArgumentException("OwnerId cannot be empty.", nameof(ownerId));
        if (!deviceId.IsValid) throw new ArgumentException("DeviceId cannot be empty.", nameof(deviceId));
        ArgumentNullException.ThrowIfNull(identityKey);
        ArgumentNullException.ThrowIfNull(signedPreKey);
        ArgumentNullException.ThrowIfNull(signedPreKeySignature);

        var bundle = new RelayHostedPreKeyBundle(ownerId, deviceId, identityKey, signedPreKey, signedPreKeySignature);
        if (oneTimePreKeys != null)
        {
            var seenIds = new HashSet<uint>();
            foreach (var key in oneTimePreKeys)
            {
                if (!seenIds.Add(key.KeyId))
                {
                    throw new ArgumentException($"Duplicate one-time prekey ID {key.KeyId}.", nameof(oneTimePreKeys));
                }
                bundle._oneTimePreKeys.Enqueue(key);
            }

            if (bundle._oneTimePreKeys.Count > MaxOneTimePreKeyCapacity)
            {
                throw new ArgumentException($"Cannot rehydrate more than {MaxOneTimePreKeyCapacity} one-time prekeys.", nameof(oneTimePreKeys));
            }
        }
        return bundle;
    }

    public static DomainResult<RelayHostedPreKeyBundle> Create(
        PublicIdentityId ownerId,
        DeviceId deviceId,
        IdentityKey identityKey,
        DhPublicKey signedPreKey,
        DeviceLinkProof signedPreKeySignature,
        IEnumerable<(uint KeyId, DhPublicKey Key)> oneTimePreKeys,
        RelayHostingPolicy policy,
        ICryptoEngine cryptoEngine)
    {
        if (policy == null || !policy.IsAcceptingPreKeys)
        {
            return DomainResult<RelayHostedPreKeyBundle>.Failure(
                new DomainError("RELAY_PREKEYS_REJECTED", "Relay is not currently accepting pre-key bundle submissions."));
        }

        if (!ownerId.IsValid || !deviceId.IsValid)
        {
            return DomainResult<RelayHostedPreKeyBundle>.Failure(
                new DomainError("INVALID_IDENTITY_OR_DEVICE", "Owner identity and device id must be valid."));
        }

        if (identityKey == null || signedPreKey == null || signedPreKeySignature == null)
        {
            return DomainResult<RelayHostedPreKeyBundle>.Failure(
                new DomainError("NULL_KEY_MATERIAL", "Identity key, signed prekey, and signature cannot be null."));
        }

        if (!cryptoEngine.VerifyEd25519Signature(identityKey, signedPreKey.Span, signedPreKeySignature.Span))
        {
            return DomainResult<RelayHostedPreKeyBundle>.Failure(
                new DomainError("INVALID_PREKEY_SIGNATURE", "Signed prekey signature failed verification against identity key."));
        }

        var bundle = new RelayHostedPreKeyBundle(ownerId, deviceId, identityKey, signedPreKey, signedPreKeySignature);

        if (oneTimePreKeys != null)
        {
            var incoming = oneTimePreKeys as IReadOnlyCollection<(uint KeyId, DhPublicKey Key)> ?? oneTimePreKeys.ToList();
            if (incoming.Count > policy.MaxOneTimePreKeysPerIdentity)
            {
                return DomainResult<RelayHostedPreKeyBundle>.Failure(
                    new DomainError("PREKEY_QUOTA_EXCEEDED", $"Cannot exceed max limit of {policy.MaxOneTimePreKeysPerIdentity} one-time prekeys."));
            }

            foreach (var key in incoming)
            {
                bundle._oneTimePreKeys.Enqueue(key);
            }
        }

        return DomainResult<RelayHostedPreKeyBundle>.Success(bundle);
    }

    public DomainResult UpdateSignedPreKey(
        DhPublicKey signedPreKey,
        DeviceLinkProof signedPreKeySignature,
        RelayHostingPolicy policy,
        ICryptoEngine cryptoEngine)
    {
        if (policy == null || !policy.IsAcceptingPreKeys)
        {
            return DomainResult.Failure(
                new DomainError("RELAY_PREKEYS_REJECTED", "Relay is not currently accepting pre-key bundle submissions."));
        }

        if (signedPreKey == null || signedPreKeySignature == null)
        {
            return DomainResult.Failure(
                new DomainError("NULL_KEY_MATERIAL", "Signed prekey and signature cannot be null."));
        }

        if (!cryptoEngine.VerifyEd25519Signature(IdentityKey, signedPreKey.Span, signedPreKeySignature.Span))
        {
            return DomainResult.Failure(
                new DomainError("INVALID_PREKEY_SIGNATURE", "Signed prekey signature failed verification against identity key."));
        }

        SignedPreKey = signedPreKey;
        SignedPreKeySignature = signedPreKeySignature;
        return DomainResult.Success();
    }

    public DomainResult ReplenishOneTimePreKeys(
        IEnumerable<(uint KeyId, DhPublicKey Key)> newPreKeys,
        RelayHostingPolicy policy)
    {
        if (policy == null || !policy.IsAcceptingPreKeys)
        {
            return DomainResult.Failure(
                new DomainError("RELAY_PREKEYS_REJECTED", "Relay is not currently accepting pre-key bundle submissions."));
        }

        if (newPreKeys == null)
        {
            return DomainResult.Success();
        }

        var incoming = newPreKeys as IReadOnlyCollection<(uint KeyId, DhPublicKey Key)> ?? newPreKeys.ToList();
        if (_oneTimePreKeys.Count + incoming.Count > policy.MaxOneTimePreKeysPerIdentity)
        {
            return DomainResult.Failure(
                new DomainError("PREKEY_QUOTA_EXCEEDED", $"Cannot exceed max limit of {policy.MaxOneTimePreKeysPerIdentity} one-time prekeys."));
        }

        foreach (var key in incoming)
        {
            _oneTimePreKeys.Enqueue(key);
        }

        return DomainResult.Success();
    }

    public DomainResult<PreKeyBundle> ConsumeBundle()
    {
        (uint KeyId, DhPublicKey Key)? oneTimeKey = null;
        if (_oneTimePreKeys.Count > 0)
        {
            oneTimeKey = _oneTimePreKeys.Dequeue();
        }

        var bundle = new PreKeyBundle(
            OwnerId,
            DeviceId,
            IdentityKey,
            SignedPreKey,
            SignedPreKeySignature,
            oneTimeKey?.Key,
            oneTimeKey?.KeyId ?? 0);

        return DomainResult<PreKeyBundle>.Success(bundle);
    }
}
