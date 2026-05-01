using System.Security.Cryptography;

namespace Percolator.Cryptography;

public interface IRatchetEngine
{
    (Ciphertext ct, RatchetEphemeralKey headerKey, RatchetState newState) Encrypt(
        RatchetState state,
        Plaintext pt,
        AssociatedData ad,
        ulong counter,
        ulong previousChainLength);

    (Plaintext pt, RatchetState newState) Decrypt(
        RatchetState state,
        SessionRatchetMessage framed,
        AssociatedData ad);
}

public sealed class AeadRatchetEngine : IRatchetEngine
{
    private const string DeriveLabel = "dr-send-derive";
    public (Ciphertext ct, RatchetEphemeralKey headerKey, RatchetState newState) Encrypt(
        RatchetState state,
        Plaintext pt,
        AssociatedData ad,
        ulong counter,
        ulong previousChainLength)
    {
        if (state is null) throw new ArgumentNullException(nameof(state));
        if (state.SendingChainKey is null) throw new InvalidOperationException("sending chain key not initialized");

        var derived = CryptoUtils.KDF(null, state.SendingChainKey.Span, DeriveLabel, CryptoUtils.KeySize * 2);
        var messageKey = new byte[CryptoUtils.KeySize];
        var nextChainKey = new byte[CryptoUtils.KeySize];
        Buffer.BlockCopy(derived, 0, messageKey, 0, CryptoUtils.KeySize);
        Buffer.BlockCopy(derived, CryptoUtils.KeySize, nextChainKey, 0, CryptoUtils.KeySize);

        RatchetEphemeralKey headerKey;
        if (state.DhRatchetPrivateKey is not null)
        {
            using var dh = ECDiffieHellman.Create();
            dh.ImportECPrivateKey(state.DhRatchetPrivateKey.Span, out _);
            headerKey = RatchetEphemeralKey.FromBytesOwned(dh.PublicKey.ExportSubjectPublicKeyInfo());
        }
        else
        {
            using var eph = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            headerKey = RatchetEphemeralKey.FromBytesOwned(eph.PublicKey.ExportSubjectPublicKeyInfo());
        }

        var adBytes = ad.ToArray();
        var ptBytes = pt.ToArray();
        var adBuf = SessionRatchetMessage.GetAssociatedData((headerKey, counter, previousChainLength), adBytes);
        var ctBytes = CryptoUtils.EncryptAesGcm(messageKey, counter, ptBytes, adBuf);
        var ct = Ciphertext.FromBytes(ctBytes);

        var newState = new RatchetState(
            state.RootKey,
            ChainKey.FromBytes(nextChainKey),
            state.SendingCounter + 1,
            state.ReceivingChainKey,
            state.ReceivingCounter,
            previousChainLength,
            state.RemoteRatchetKey,
            state.DhRatchetPrivateKey,
            state.SkippedKeyLimit);

        return (ct, headerKey, newState);
    }

    public (Plaintext pt, RatchetState newState) Decrypt(
        RatchetState state,
        SessionRatchetMessage framed,
        AssociatedData ad)
    {
        if (state is null) throw new ArgumentNullException(nameof(state));
        if (framed is null) throw new ArgumentNullException(nameof(framed));
        if (state.ReceivingChainKey is null) throw new InvalidOperationException("receiving chain key not initialized");

        var (preKey, counter, prevLen) = framed.GetHeader();
        if (preKey.Span.Length == 0) throw new ArgumentException("invalid header key");
        var adBytes = ad.ToArray();
        var adBuf = SessionRatchetMessage.GetAssociatedData((preKey, counter, prevLen), adBytes);

        var derived = CryptoUtils.KDF(null, state.ReceivingChainKey.Span, DeriveLabel, CryptoUtils.KeySize * 2);
        var messageKey = new byte[CryptoUtils.KeySize];
        var nextChainKey = new byte[CryptoUtils.KeySize];
        Buffer.BlockCopy(derived, 0, messageKey, 0, CryptoUtils.KeySize);
        Buffer.BlockCopy(derived, CryptoUtils.KeySize, nextChainKey, 0, CryptoUtils.KeySize);

        var ctBytes = framed.GetCiphertext().ToArray();
        var ptBytes = CryptoUtils.DecryptAesGcm(messageKey, counter, ctBytes, adBuf);
        var newState = new RatchetState(
            state.RootKey,
            state.SendingChainKey,
            state.SendingCounter,
            ChainKey.FromBytes(nextChainKey),
            state.ReceivingCounter + 1,
            prevLen,
            state.RemoteRatchetKey,
            state.DhRatchetPrivateKey,
            state.SkippedKeyLimit);

        return (Plaintext.FromBytes(ptBytes), newState);
    }
}
