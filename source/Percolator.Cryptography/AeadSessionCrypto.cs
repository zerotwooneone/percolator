using System.Security.Cryptography;

namespace Percolator.Cryptography;

public sealed class AeadSessionCrypto : ISessionCrypto
{
    private readonly IX3dhDeriver _x3dh;
    private readonly IRatchetEngine _ratchet;
    private readonly IPreKeyBundleValidator _validator;

    public AeadSessionCrypto()
        : this(new X3dhDeriver(), new AeadRatchetEngine(), new PreKeyBundleValidator())
    {
    }

    public AeadSessionCrypto(IX3dhDeriver x3dh, IRatchetEngine ratchet, IPreKeyBundleValidator validator)
    {
        _x3dh = x3dh ?? throw new ArgumentNullException(nameof(x3dh));
        _ratchet = ratchet ?? throw new ArgumentNullException(nameof(ratchet));
        _validator = validator ?? throw new ArgumentNullException(nameof(validator));
    }

    public (SharedSecret SharedSecret, RatchetEphemeralKey EphemeralPublic) X3DH_Initiate(PrivatePreKey localIdentityPrivate, PreKeyBundle remoteBundle)
    {
        if (localIdentityPrivate?.Value is null || localIdentityPrivate.Value.Length == 0)
            throw new ArgumentException("local identity private key missing", nameof(localIdentityPrivate));
        if (remoteBundle is null)
            throw new ArgumentNullException(nameof(remoteBundle));

        _validator.Validate(remoteBundle);

        var init = _x3dh.DeriveInitiator(
            remoteBundle.IdentitySigningKey,
            remoteBundle.SignedPreKey,
            remoteBundle.OneTimePreKey,
            localIdentityPrivate);

        return (init.InitialRootKey, init.InitiatorEphemeralPublicKey);
    }

    public (Ciphertext Ciphertext, RatchetEphemeralKey HeaderKey, RatchetState NewState) DR_Encrypt(
        RatchetState state,
        Plaintext pt,
        AssociatedData ad,
        ulong counter,
        ulong previousChainLength)
    {
        var (ct, header, newState) = _ratchet.Encrypt(state, pt, ad, counter, previousChainLength);
        return (ct, header, newState);
    }

    public (Plaintext Plaintext, RatchetState NewState) DR_Decrypt(RatchetState state, SessionRatchetMessage framed, AssociatedData ad)
    {
        return _ratchet.Decrypt(state, framed, ad);
    }

    public bool VerifySignature(RatchetIdentityKey identityPublic, PreKey signedPreKey, Signature signature)
    {
        if (identityPublic?.Value is null || signedPreKey?.Value is null || signature?.Value is null)
            return false;
        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(identityPublic.Value, out _);
            return ecdsa.VerifyData(signedPreKey.Value, signature.Value, HashAlgorithmName.SHA256);
        }
        catch
        {
            return false;
        }
    }

    public SharedSecret X3DH_Respond(
        RatchetIdentityKey initiatorId,
        RatchetEphemeralKey initiatorEph,
        PrivatePreKey localIdentityPrivate,
        PrivatePreKey localSpkPrivate,
        PrivatePreKey? localOtkPrivate)
    {
        var resp = _x3dh.DeriveResponder(initiatorId, initiatorEph, localIdentityPrivate, localSpkPrivate, localOtkPrivate);
        return resp.InitialRootKey;
    }
}
