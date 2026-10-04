using Percolator.Domain.Common;
using Percolator.Domain.Delivery.Hosting;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.ValueObjects;
using Percolator.Domain.IntegrationTests.TestDoubles;

namespace Percolator.Domain.IntegrationTests.Builders;

/// <summary>
/// Builder helper modeling a complete test participant with identity keys, signed pre-keys,
/// one-time pre-key pools, and an in-memory private prekey store.
/// </summary>
public sealed class ScenarioParticipant : IDisposable
{
    public string Name { get; }
    public PublicIdentityId IdentityId { get; }
    public DeviceId DeviceId { get; }
    public EphemeralPrivateKey IdentityPrivateKey { get; }
    public IdentityKey IdentityPublicKey { get; }
    public EphemeralPrivateKey SignedPreKeyPrivate { get; }
    public DhPublicKey SignedPreKeyPublic { get; }
    public byte[] SignedPreKeySignature { get; }
    public InMemoryPrivatePreKeyStore PreKeyStore { get; }
    public List<(uint KeyId, DhPublicKey PublicKey, EphemeralPrivateKey PrivateKey)> OneTimePreKeys { get; } = new();

    public ScenarioParticipant(
        string name,
        PublicIdentityId identityId,
        DeviceId deviceId,
        EphemeralPrivateKey identityPrivateKey,
        IdentityKey identityPublicKey,
        EphemeralPrivateKey signedPreKeyPrivate,
        DhPublicKey signedPreKeyPublic,
        byte[] signedPreKeySignature,
        InMemoryPrivatePreKeyStore preKeyStore)
    {
        Name = name;
        IdentityId = identityId;
        DeviceId = deviceId;
        IdentityPrivateKey = identityPrivateKey;
        IdentityPublicKey = identityPublicKey;
        SignedPreKeyPrivate = signedPreKeyPrivate;
        SignedPreKeyPublic = signedPreKeyPublic;
        SignedPreKeySignature = signedPreKeySignature;
        PreKeyStore = preKeyStore;
    }

    public static ScenarioParticipant Create(
        string name,
        ScenarioCryptoEngine engine,
        DeviceId? deviceId = null,
        int opkPoolSize = 0)
    {
        var id = PublicIdentityId.New();
        var devId = deviceId ?? DeviceId.Primary;
        var (idPriv, idPub) = engine.GenerateIdentityKeyPair();
        var (spkPriv, spkPub) = engine.GenerateEphemeralKeyPair();
        var sig = engine.SignEd25519(idPriv.Span, spkPub.Span);

        var store = new InMemoryPrivatePreKeyStore();
        store.StoreSignedPreKeyPrivateAsync(id, devId, spkPriv).GetAwaiter().GetResult();

        var participant = new ScenarioParticipant(
            name, id, devId, idPriv, idPub, spkPriv, spkPub, sig, store);

        if (opkPoolSize > 0)
        {
            var opkList = new List<(uint KeyId, EphemeralPrivateKey Key)>();
            for (uint i = 1; i <= opkPoolSize; i++)
            {
                var (priv, pub) = engine.GenerateEphemeralKeyPair();
                participant.OneTimePreKeys.Add((i, pub, priv));
                opkList.Add((i, priv));
            }
            store.StoreOneTimePreKeysPrivateAsync(id, devId, opkList).GetAwaiter().GetResult();
        }

        return participant;
    }

    /// <summary>
    /// Models adding an additional device to this participant's identity (e.g. Mobile alongside Desktop).
    /// Shares the primary IdentityKey and IdentityPrivateKey, but provisions independent device-level signed pre-keys.
    /// </summary>
    public ScenarioParticipant CreateAdditionalDevice(
        string deviceName,
        DeviceId deviceId,
        ScenarioCryptoEngine engine,
        int opkPoolSize = 0)
    {
        var (spkPriv, spkPub) = engine.GenerateEphemeralKeyPair();
        var sig = engine.SignEd25519(IdentityPrivateKey.Span, spkPub.Span);

        var store = new InMemoryPrivatePreKeyStore();
        store.StoreSignedPreKeyPrivateAsync(IdentityId, deviceId, spkPriv).GetAwaiter().GetResult();

        var idPrivBytes = IdentityPrivateKey.Span.ToArray();
        var idPrivCopy = EphemeralPrivateKey.FromSpan(idPrivBytes);

        var devParticipant = new ScenarioParticipant(
            $"{Name} ({deviceName})", IdentityId, deviceId, idPrivCopy, IdentityPublicKey, spkPriv, spkPub, sig, store);

        if (opkPoolSize > 0)
        {
            var opkList = new List<(uint KeyId, EphemeralPrivateKey Key)>();
            for (uint i = 1; i <= opkPoolSize; i++)
            {
                var (priv, pub) = engine.GenerateEphemeralKeyPair();
                devParticipant.OneTimePreKeys.Add((i, pub, priv));
                opkList.Add((i, priv));
            }
            store.StoreOneTimePreKeysPrivateAsync(IdentityId, deviceId, opkList).GetAwaiter().GetResult();
        }

        return devParticipant;
    }

    public PreKeyBundle CreatePreKeyBundle(uint? opkId = null)
    {
        DhPublicKey? opkPub = null;
        uint keyId = 0;
        if (opkId.HasValue)
        {
            var found = OneTimePreKeys.FirstOrDefault(k => k.KeyId == opkId.Value);
            if (found.PublicKey != null)
            {
                opkPub = found.PublicKey;
                keyId = found.KeyId;
            }
        }

        return new PreKeyBundle(
            IdentityId,
            DeviceId,
            IdentityPublicKey,
            SignedPreKeyPublic,
            DeviceLinkProof.FromSpan(SignedPreKeySignature),
            opkPub,
            keyId);
    }

    /// <summary>
    /// Creates a RelayHostedPreKeyBundle aggregate ready to be hosted on a relay directory.
    /// </summary>
    public RelayHostedPreKeyBundle CreateHostedPreKeyBundle(
        ScenarioCryptoEngine engine,
        RelayHostingPolicy? policy = null)
    {
        var opkPubs = OneTimePreKeys.Select(k => (k.KeyId, k.PublicKey)).ToList();
        return RelayHostedPreKeyBundle.Create(
            IdentityId,
            DeviceId,
            IdentityPublicKey,
            SignedPreKeyPublic,
            DeviceLinkProof.FromSpan(SignedPreKeySignature),
            opkPubs,
            policy ?? RelayHostingPolicy.Default,
            engine).Value!;
    }

    public void Dispose()
    {
        IdentityPrivateKey.Dispose();
        SignedPreKeyPrivate.Dispose();
        foreach (var opk in OneTimePreKeys)
        {
            opk.PrivateKey.Dispose();
        }
    }
}
