using Percolator.Domain.Common;
using Percolator.Domain.Identities.ValueObjects;

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
    public override PublicIdentityId Id => RemotePeerId;
    public PublicIdentityId OwnerIdentityId { get; }
    public PublicIdentityId RemotePeerId { get; }
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
        DateTimeOffset createdAtUtc)
    {
        OwnerIdentityId = ownerIdentityId;
        RemotePeerId = remotePeerId;
        Nickname = nickname;
        TrustLevel = trustLevel;
        CreatedAtUtc = createdAtUtc;
    }

    public void UpdateTrust(PeerTrustLevel trustLevel)
    {
        TrustLevel = trustLevel;
    }

    public void RegisterDevice(DeviceId deviceId)
    {
        _registeredDevices.Add(deviceId);
    }

    public void RecordActivity(DateTimeOffset timestampUtc)
    {
        LastSeenAtUtc = timestampUtc;
    }
}
