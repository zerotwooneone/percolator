using System.Buffers.Binary;
using Percolator.Domain.Common;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.Ports;

namespace Percolator.Domain.Identities.Model;

public enum PeerTrustLevel
{
    Untrusted = 0,
    Tofu = 1,
    Verified = 2,
    Blocked = 3
}

public sealed class PeerContact : AggregateRoot<PublicIdentityId>
{
    public const int MaxRegisteredDevicesPerPeer = 32;

    public override PublicIdentityId Id => RemotePeerId;
    public PublicIdentityId OwnerIdentityId { get; }
    public PublicIdentityId RemotePeerId { get; }
    public IdentityPublicKey? PrimaryPublicKey { get; private set; }
    public string Nickname { get; private set; }
    public PeerTrustLevel TrustLevel { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset? LastSeenAtUtc { get; private set; }

    private readonly HashSet<DeviceId> _registeredDevices = [];
    public IReadOnlySet<DeviceId> RegisteredDevices => _registeredDevices;

    public PeerContact(
        PublicIdentityId ownerIdentityId,
        PublicIdentityId remotePeerId,
        string nickname,
        PeerTrustLevel trustLevel,
        DateTimeOffset createdAtUtc,
        IdentityPublicKey? primaryPublicKey = null)
    {
        OwnerIdentityId = ownerIdentityId;
        RemotePeerId = remotePeerId;
        Nickname = nickname;
        TrustLevel = trustLevel;
        CreatedAtUtc = createdAtUtc;
        PrimaryPublicKey = primaryPublicKey;

        // Primary device is implicitly registered
        _registeredDevices.Add(DeviceId.Primary);
    }

    public void UpdateTrust(PeerTrustLevel trustLevel)
    {
        TrustLevel = trustLevel;
    }

    public DomainResult RotatePrimaryPublicKey(IdentityPublicKey newKey)
    {
        if (newKey == null)
        {
            return DomainResult.Failure(new DomainError("NULL_PUBLIC_KEY", "New primary public key cannot be null."));
        }

        if (PrimaryPublicKey != null && PrimaryPublicKey.Equals(newKey))
        {
            return DomainResult.Success();
        }

        PrimaryPublicKey = newKey;
        // Signal protocol: When an identity key changes, safety number changes -> trust drops to Untrusted.
        TrustLevel = PeerTrustLevel.Untrusted;
        return DomainResult.Success();
    }

    public DomainResult RegisterSecondaryDevice(
        DeviceId deviceId,
        IdentityPublicKey secondaryDevicePublicKey,
        DeviceLinkProof linkProof,
        IdentityPublicKey primaryPeerPublicKey,
        ICryptoEngine cryptoEngine)
    {
        if (deviceId.IsPrimary || !deviceId.IsValid)
        {
            return DomainResult.Failure(new DomainError("INVALID_DEVICE_ID", "Secondary device must have a valid non-zero DeviceId and cannot be Primary DeviceId(1)."));
        }

        if (_registeredDevices.Contains(deviceId))
        {
            return DomainResult.Success();
        }

        if (_registeredDevices.Count >= MaxRegisteredDevicesPerPeer)
        {
            return DomainResult.Failure(new DomainError("MAX_DEVICES_EXCEEDED", $"Cannot register more than {MaxRegisteredDevicesPerPeer} devices for a peer."));
        }

        Span<byte> messageToVerify = stackalloc byte[4 + 32];
        BinaryPrimitives.WriteUInt32LittleEndian(messageToVerify[..4], deviceId.Value);
        secondaryDevicePublicKey.Span.CopyTo(messageToVerify[4..]);

        if (!cryptoEngine.VerifyEd25519Signature(primaryPeerPublicKey, messageToVerify, linkProof.Span))
        {
            return DomainResult.Failure(new DomainError("INVALID_LINK_PROOF_SIGNATURE", "The device link proof signature is invalid or forged."));
        }

        _registeredDevices.Add(deviceId);
        return DomainResult.Success();
    }

    public void RecordActivity(DateTimeOffset timestampUtc)
    {
        LastSeenAtUtc = timestampUtc;
    }
}
