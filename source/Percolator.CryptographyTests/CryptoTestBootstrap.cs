using Percolator.Cryptography;

namespace Percolator.CryptographyTests;

public static class CryptoTestBootstrap
{
    // Derive initial send/recv chain keys from a root for bootstrap
    public static (ChainKey Send, ChainKey Recv) DeriveInitialChainsFromRoot(RootKey root)
    {
        var sendCk = new ChainKey(CryptoUtils.KDF(null, root.Value, "dr-send-init", CryptoUtils.KeySize));
        var recvCk = new ChainKey(CryptoUtils.KDF(null, root.Value, "dr-recv-init", CryptoUtils.KeySize));
        return (sendCk, recvCk);
    }

    // Create complementary ratchet states for a communicating pair (initiator/responder)
    public static (RatchetState Initiator, RatchetState Responder) CreatePairedStates(RootKey root, int skippedKeyLimit = 1000)
    {
        var (sendCk, recvCk) = DeriveInitialChainsFromRoot(root);
        var initiator = new RatchetState(root, sendCk, 0, recvCk, 0, 0, null, null, skippedKeyLimit);
        var responder = new RatchetState(root, recvCk, 0, sendCk, 0, 0, null, null, skippedKeyLimit);
        return (initiator, responder);
    }

    // Create a ready state with derived initial chain keys and zeroed counters
    public static RatchetState CreateBootstrappedState(RootKey root, int skippedKeyLimit = 1000)
    {
        var (sendCk, recvCk) = DeriveInitialChainsFromRoot(root);
        return new RatchetState(root, sendCk, 0, recvCk, 0, 0, null, null, skippedKeyLimit);
    }

    // Create a state with explicit fields
    public static RatchetState CreateState(
        RootKey root,
        ChainKey sendCk,
        ulong sendCounter,
        ChainKey recvCk,
        ulong recvCounter,
        ulong previousChainLength = 0,
        RatchetEphemeralKey? remoteRatchetKey = null,
        PrivateEphemeralKey? dhRatchetPrivateKey = null,
        int skippedKeyLimit = 1000)
    {
        return new RatchetState(
            root,
            sendCk,
            sendCounter,
            recvCk,
            recvCounter,
            previousChainLength,
            remoteRatchetKey,
            dhRatchetPrivateKey,
            skippedKeyLimit);
    }

    // Convenience: frame a ciphertext with given header fields
    public static SessionRatchetMessage Frame(RatchetEphemeralKey headerKey, ulong counter, ulong prevLen, Ciphertext ct)
    {
        return SessionRatchetMessage.Create(headerKey, counter, prevLen, ct);
    }

    // Convenience: build associated data buffer for header + custom AD
    public static byte[] BuildHeaderAd(RatchetEphemeralKey headerKey, ulong counter, ulong prevLen, byte[] ad)
    {
        return SessionRatchetMessage.GetAssociatedData((headerKey, counter, prevLen), ad);
    }

    // Generate a new DH private key for ratchet and return domain VO
    public static PrivateEphemeralKey NewDhPrivateKey()
    {
        using var dh = System.Security.Cryptography.ECDiffieHellman.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        return new PrivateEphemeralKey(dh.ExportECPrivateKey());
    }

    // Convert a DH private key to its public counterpart VO
    public static RatchetEphemeralKey ToPublic(PrivateEphemeralKey priv)
    {
        using var dh = System.Security.Cryptography.ECDiffieHellman.Create();
        dh.ImportECPrivateKey(priv.Value, out _);
        return new RatchetEphemeralKey(dh.PublicKey.ExportSubjectPublicKeyInfo());
    }
}
