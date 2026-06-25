namespace Percolator.Identity.Model;

public sealed class SelfIdentity
{
    private readonly List<IdentityKey> _keys = new();

    public SelfId Id { get; }
    public DisplayName? DisplayName { get; private set; }
    public IReadOnlyList<IdentityKey> Keys => _keys;
    public DateTimeOffset LastUsedUtc { get; private set; }
    public PublicIdentityId PublicIdentityId { get; }
    public ListeningPort ListeningPort { get; private set; }
    public DeviceId DeviceId { get; private set; } = DeviceId.Primary;
    public ProfileKeyBytes? CurrentProfileKey { get; private set; }
    public ProfileCiphertextPackage? CurrentProfileCiphertext { get; private set; }
    public int ProfileRevision { get; private set; }
    public Percolator.Identity.RelayRootKeyBytes? RelayDeliveryRootKey { get; private set; }

    public SelfIdentity(SelfId id, PublicIdentityId publicIdentityId, ListeningPort listeningPort,DeviceId deviceId, DateTimeOffset lastUsedUtc)
    {
        Id = id;
        PublicIdentityId = publicIdentityId;
        ListeningPort = listeningPort;
        LastUsedUtc = lastUsedUtc; // Explicit non-nullable; caller should set via TouchLastUsed
        DeviceId = deviceId;
    }

    public void SetDisplayName(DisplayName name) => DisplayName = name;

    public void SetDisplayName(string name) => DisplayName = new DisplayName(name);

    public void AddKey(byte[] spki, DateTimeOffset notBefore, DateTimeOffset expiresAt, DateTimeOffset now)
    {
        var newKey = new IdentityKey(spki, notBefore, expiresAt);
        var newActiveNow = newKey.IsActiveAt(now);
        if (newActiveNow && _keys.Any(k => k.IsActiveAt(now)))
            throw new InvalidOperationException("Overlapping active key windows are not allowed at the current time.");
        _keys.Add(newKey);
    }

    public IdentityKey? GetActiveKey(DateTimeOffset when)
        => _keys.FirstOrDefault(k => k.IsActiveAt(when));

    public IdentityKey? GetNextScheduledKey(DateTimeOffset when)
        => _keys.Where(k => k.NotBefore > when).OrderBy(k => k.NotBefore).FirstOrDefault();

    public void TouchLastUsed(DateTimeOffset when) => LastUsedUtc = when;

    public void UpdateListeningPort(ListeningPort port) => ListeningPort = port;

    public void CommitProfileUpdate(ProfileKeyBytes newKey, ProfileCiphertextPackage payload)
    {
        CurrentProfileKey = newKey;
        CurrentProfileCiphertext = payload;
        ProfileRevision++;
    }

    public void EnableRelayMode(Percolator.Identity.RelayRootKeyBytes rootKey)
    {
        RelayDeliveryRootKey = rootKey;
    }
}
