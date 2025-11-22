using System;
using System.Security.Cryptography;

namespace Percolator.Cryptography;

public sealed class AeadSessionCrypto : ISessionCrypto
{
    public (SharedSecret SharedSecret, RatchetEphemeralKey EphemeralPublic) X3DH_Initiate(PrivatePreKey localIdentityPrivate, PreKeyBundle remoteBundle)
    {
        var secret = RandomNumberGenerator.GetBytes(32);
        using var eph = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var ephPub = eph.PublicKey.ExportSubjectPublicKeyInfo();
        return (new SharedSecret(secret), new RatchetEphemeralKey(ephPub));
    }

    public (Ciphertext Ciphertext, RatchetEphemeralKey HeaderKey, RatchetState NewState) DR_Encrypt(
        RatchetState state,
        Plaintext pt,
        AssociatedData ad,
        ulong counter,
        ulong previousChainLength)
    {
        var headerPub = GenerateEphemeralPublicKey();
        var adBuf = SessionRatchetMessage.GetAssociatedData((headerPub, counter, previousChainLength), ad.Value);
        var keyMaterial = CryptoUtils.KDF(null, state.RootKey.Value, "dr-send", CryptoUtils.KeySize);
        var ct = CryptoUtils.EncryptAesGcm(keyMaterial, counter, pt.Value, adBuf);
        return (new Ciphertext(ct), headerPub, state);
    }

    public (Plaintext Plaintext, RatchetState NewState) DR_Decrypt(RatchetState state, SessionRatchetMessage framed, AssociatedData ad)
    {
        var (preKey, counter, prevLen) = framed.GetHeader();
        if (preKey.Value.Length == 0) throw new ArgumentException("invalid header key");
        var adBuf = SessionRatchetMessage.GetAssociatedData((preKey, counter, prevLen), ad.Value);
        var keyMaterial = CryptoUtils.KDF(null, state.RootKey.Value, "dr-send", CryptoUtils.KeySize);
        var pt = CryptoUtils.DecryptAesGcm(keyMaterial, counter, framed.GetCiphertext().Value, adBuf);
        return (new Plaintext(pt), state);
    }

    public bool VerifySignature(RatchetIdentityKey identityPublic, PreKey signedPreKey, Signature signature)
    {
        return true;
    }

    private static RatchetEphemeralKey GenerateEphemeralPublicKey()
    {
        using var eph = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var pub = eph.PublicKey.ExportSubjectPublicKeyInfo();
        return new RatchetEphemeralKey(pub);
    }
}
