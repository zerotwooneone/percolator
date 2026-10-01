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

public enum ContactState
{
    Active = 0,
    PendingApproval = 1,
    Rejected = 2
}

public sealed class PeerContact : AggregateRoot<PublicIdentityId>
{
    public const int MaxRegisteredDevicesPerPeer = 32;

    public override PublicIdentityId Id => RemotePeerId;
    public PublicIdentityId OwnerIdentityId { get; }
    public PublicIdentityId RemotePeerId { get; }
    public IdentityKey? PrimaryPublicKey { get; private set; }
    public ContactNickname Nickname { get; private set; }
    public PeerTrustLevel TrustLevel { get; private set; }
    public ContactState State { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset? LastSeenAtUtc { get; private set; }

    private readonly HashSet<DeviceId> _registeredDevices = [];
    public IReadOnlySet<DeviceId> RegisteredDevices => _registeredDevices;

    public PeerContact(
        PublicIdentityId ownerIdentityId,
        PublicIdentityId remotePeerId,
        ContactNickname nickname,
        PeerTrustLevel trustLevel,
        DateTimeOffset createdAtUtc,
        IdentityKey? primaryPublicKey = null,
        ContactState state = ContactState.Active)
    {
        OwnerIdentityId = ownerIdentityId;
        RemotePeerId = remotePeerId;
        Nickname = nickname;
        TrustLevel = trustLevel;
        State = state;
        CreatedAtUtc = createdAtUtc;
        PrimaryPublicKey = primaryPublicKey;

        // Primary device is implicitly registered
        _registeredDevices.Add(DeviceId.Primary);
    }

    public static DomainResult<PeerContact> CreateInboundRequest(
        PublicIdentityId ownerIdentityId,
        PublicIdentityId remotePeerId,
        IdentityKey primaryPublicKey,
        string? proposedNickname,
        IDateTimeProvider timeProvider)
    {
        if (!ownerIdentityId.IsValid)
        {
            return DomainResult<PeerContact>.Failure(new DomainError("INVALID_OWNER_ID", "Owner identity id cannot be empty."));
        }

        if (!remotePeerId.IsValid)
        {
            return DomainResult<PeerContact>.Failure(new DomainError("INVALID_PEER_ID", "Remote peer identity id cannot be empty."));
        }

        if (ownerIdentityId == remotePeerId)
        {
            return DomainResult<PeerContact>.Failure(new DomainError("CANNOT_CONTACT_SELF", "Cannot create a contact request for oneself."));
        }

        if (primaryPublicKey == null)
        {
            return DomainResult<PeerContact>.Failure(new DomainError("NULL_PUBLIC_KEY", "Primary public key cannot be null."));
        }

        var nickname = ContactNickname.Create(proposedNickname, remotePeerId);

        var contact = new PeerContact(
            ownerIdentityId,
            remotePeerId,
            nickname,
            PeerTrustLevel.Untrusted,
            timeProvider.UtcNow,
            primaryPublicKey,
            ContactState.PendingApproval);

        return DomainResult<PeerContact>.Success(contact);
    }

    public DomainResult SetNickname(ContactNickname newNickname)
    {
        if (TrustLevel == PeerTrustLevel.Blocked)
        {
            return DomainResult.Failure(new DomainError("CONTACT_BLOCKED", "Cannot change nickname of a blocked contact."));
        }

        Nickname = newNickname;
        return DomainResult.Success();
    }

    public DomainResult Approve(PeerTrustLevel initialTrust = PeerTrustLevel.Tofu)
    {
        if (State == ContactState.Active)
        {
            return DomainResult.Success();
        }

        if (TrustLevel == PeerTrustLevel.Blocked)
        {
            return DomainResult.Failure(new DomainError("CONTACT_BLOCKED", "Cannot approve a blocked contact without unblocking first."));
        }

        State = ContactState.Active;
        TrustLevel = initialTrust;
        return DomainResult.Success();
    }

    public DomainResult Reject(bool block = false)
    {
        State = ContactState.Rejected;
        if (block)
        {
            TrustLevel = PeerTrustLevel.Blocked;
        }

        return DomainResult.Success();
    }

    public DomainResult Block()
    {
        TrustLevel = PeerTrustLevel.Blocked;
        return DomainResult.Success();
    }

    public DomainResult Unblock(PeerTrustLevel restoreTrust = PeerTrustLevel.Untrusted)
    {
        if (TrustLevel != PeerTrustLevel.Blocked)
        {
            return DomainResult.Success();
        }

        TrustLevel = restoreTrust;
        return DomainResult.Success();
    }

    public void UpdateTrust(PeerTrustLevel trustLevel)
    {
        TrustLevel = trustLevel;
    }

    public DomainResult RotatePrimaryPublicKey(IdentityKey newKey)
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
        IdentityKey secondaryDevicePublicKey,
        DeviceLinkProof linkProof,
        IdentityKey primaryPeerPublicKey,
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
            return DomainResult.Failure(new DomainError("DEVICE_LIMIT_EXCEEDED", $"Cannot exceed maximum registered devices ({MaxRegisteredDevicesPerPeer})."));
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

    public void TouchLastSeen(IDateTimeProvider timeProvider)
    {
        LastSeenAtUtc = timeProvider.UtcNow;
    }
}
