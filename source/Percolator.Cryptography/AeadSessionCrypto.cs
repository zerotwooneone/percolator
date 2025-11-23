using System;
using System.Security.Cryptography;

namespace Percolator.Cryptography;

public sealed class AeadSessionCrypto : ISessionCrypto
{
    private const string DeriveLabel_Send = "dr-send-derive";

    public (SharedSecret SharedSecret, RatchetEphemeralKey EphemeralPublic) X3DH_Initiate(PrivatePreKey localIdentityPrivate, PreKeyBundle remoteBundle)
    {
        if (localIdentityPrivate?.Value is null || localIdentityPrivate.Value.Length == 0)
            throw new ArgumentException("local identity private key missing", nameof(localIdentityPrivate));
        if (remoteBundle is null)
            throw new ArgumentNullException(nameof(remoteBundle));

        // 1) Verify SPK signature (defensive)
        if (!VerifySignature(remoteBundle.IdentitySigningKey, remoteBundle.SignedPreKey, remoteBundle.SignedPreKeySignature))
            throw new CryptographicException("remote signed pre-key signature invalid");

        // 2) Import keys
        using var ikA = ECDiffieHellman.Create();
        ikA.ImportECPrivateKey(localIdentityPrivate.Value, out _);
        using var ikB = ECDiffieHellman.Create();
        ikB.ImportSubjectPublicKeyInfo(remoteBundle.IdentitySigningKey.Value, out _);
        using var spkB = ECDiffieHellman.Create();
        spkB.ImportSubjectPublicKeyInfo(remoteBundle.SignedPreKey.Value, out _);
        using var opkB = remoteBundle.OneTimePreKey is null ? null : ECDiffieHellman.Create();
        if (opkB is not null)
        {
            opkB!.ImportSubjectPublicKeyInfo(remoteBundle.OneTimePreKey!.Value, out _);
        }

        // 3) Generate initiator ephemeral key EK_A
        using var ekA = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var ekA_pub_spki = ekA.PublicKey.ExportSubjectPublicKeyInfo();

        // 4) Compute DH tuples
        var dh1 = ikA.DeriveKeyMaterial(spkB.PublicKey); // DH(IK_A, SPK_B)
        var dh2 = ekA.DeriveKeyMaterial(ikB.PublicKey);  // DH(EK_A, IK_B)
        var dh3 = ekA.DeriveKeyMaterial(spkB.PublicKey); // DH(EK_A, SPK_B)
        byte[]? dh4 = null;
        if (opkB is not null)
        {
            dh4 = ekA.DeriveKeyMaterial(opkB.PublicKey); // DH(EK_A, OPK_B)
        }

        // 5) HKDF over concatenation (pad optional). Use stable info.
        var concatLen = dh1.Length + dh2.Length + dh3.Length + (dh4?.Length ?? 0);
        var input = new byte[concatLen];
        Buffer.BlockCopy(dh1, 0, input, 0, dh1.Length);
        Buffer.BlockCopy(dh2, 0, input, dh1.Length, dh2.Length);
        Buffer.BlockCopy(dh3, 0, input, dh1.Length + dh2.Length, dh3.Length);
        if (dh4 is not null)
        {
            Buffer.BlockCopy(dh4, 0, input, dh1.Length + dh2.Length + dh3.Length, dh4.Length);
        }
        var irk = CryptoUtils.KDF(null, input, "x3dh", CryptoUtils.KeySize);

        return (new SharedSecret(irk), new RatchetEphemeralKey(ekA_pub_spki));
    }

    public (Ciphertext Ciphertext, RatchetEphemeralKey HeaderKey, RatchetState NewState) DR_Encrypt(
        RatchetState state,
        Plaintext pt,
        AssociatedData ad,
        ulong counter,
        ulong previousChainLength)
    {
        if (state is null) throw new ArgumentNullException(nameof(state));
        if (state.SendingChainKey is null) throw new InvalidOperationException("sending chain key not initialized");

        // Derive message key and next chain key from current sending chain key
        var derived = CryptoUtils.KDF(null, state.SendingChainKey.Value, DeriveLabel_Send, CryptoUtils.KeySize * 2);
        var messageKey = new byte[CryptoUtils.KeySize];
        var nextChainKey = new byte[CryptoUtils.KeySize];
        Buffer.BlockCopy(derived, 0, messageKey, 0, CryptoUtils.KeySize);
        Buffer.BlockCopy(derived, CryptoUtils.KeySize, nextChainKey, 0, CryptoUtils.KeySize);

        // Header key: use current DH ratchet public key if present; otherwise generate a fresh ephemeral pub
        RatchetEphemeralKey headerKey;
        if (state.DhRatchetPrivateKey is not null)
        {
            using var dh = ECDiffieHellman.Create();
            dh.ImportECPrivateKey(state.DhRatchetPrivateKey.Value, out _);
            headerKey = new RatchetEphemeralKey(dh.PublicKey.ExportSubjectPublicKeyInfo());
        }
        else
        {
            using var eph = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            headerKey = new RatchetEphemeralKey(eph.PublicKey.ExportSubjectPublicKeyInfo());
        }

        var adBuf = SessionRatchetMessage.GetAssociatedData((headerKey, counter, previousChainLength), ad.Value);
        var ctBytes = CryptoUtils.EncryptAesGcm(messageKey, counter, pt.Value, adBuf);
        var ct = new Ciphertext(ctBytes);

        var newState = new RatchetState(
            state.RootKey,
            new ChainKey(nextChainKey),
            state.SendingCounter + 1,
            state.ReceivingChainKey,
            state.ReceivingCounter,
            previousChainLength,
            state.RemoteRatchetKey,
            state.DhRatchetPrivateKey,
            state.SkippedKeyLimit);

        return (ct, headerKey, newState);
    }

    public (Plaintext Plaintext, RatchetState NewState) DR_Decrypt(RatchetState state, SessionRatchetMessage framed, AssociatedData ad)
    {
        if (state is null) throw new ArgumentNullException(nameof(state));
        if (framed is null) throw new ArgumentNullException(nameof(framed));
        if (state.ReceivingChainKey is null) throw new InvalidOperationException("receiving chain key not initialized");

        var (preKey, counter, prevLen) = framed.GetHeader();
        if (preKey.Value is null || preKey.Value.Length == 0)
            throw new ArgumentException("header ratchet key is invalid", nameof(framed));
        var adBuf = SessionRatchetMessage.GetAssociatedData((preKey, counter, prevLen), ad.Value);

        var derived = CryptoUtils.KDF(null, state.ReceivingChainKey.Value, DeriveLabel_Send, CryptoUtils.KeySize * 2);
        var messageKey = new byte[CryptoUtils.KeySize];
        var nextChainKey = new byte[CryptoUtils.KeySize];
        Buffer.BlockCopy(derived, 0, messageKey, 0, CryptoUtils.KeySize);
        Buffer.BlockCopy(derived, CryptoUtils.KeySize, nextChainKey, 0, CryptoUtils.KeySize);

        var ptBytes = CryptoUtils.DecryptAesGcm(messageKey, counter, framed.GetCiphertext().Value, adBuf);
        var newState = new RatchetState(
            state.RootKey,
            state.SendingChainKey,
            state.SendingCounter,
            new ChainKey(nextChainKey),
            state.ReceivingCounter + 1,
            prevLen,
            state.RemoteRatchetKey,
            state.DhRatchetPrivateKey,
            state.SkippedKeyLimit);

        return (new Plaintext(ptBytes), newState);
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
}
