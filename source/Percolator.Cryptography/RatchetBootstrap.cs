using Percolator.Cryptography.Primitives;

namespace Percolator.Cryptography;

public static class RatchetBootstrap
{
    private const string LabelInitSend = "dr-send-init";
    private const string LabelInitRecv = "dr-recv-init";

    public static (ChainKey Send, ChainKey Recv) DeriveInitiatorChains(RootKey root)
    {
        var send = ChainKey.FromBytes(CryptoUtils.KDF(null, root.ToArray(), LabelInitSend, CryptoUtils.KeySize));
        var recv = ChainKey.FromBytes(CryptoUtils.KDF(null, root.ToArray(), LabelInitRecv, CryptoUtils.KeySize));
        return (send, recv);
    }

    public static (ChainKey Send, ChainKey Recv) DeriveResponderChains(RootKey root)
    {
        var send = ChainKey.FromBytes(CryptoUtils.KDF(null, root.ToArray(), LabelInitRecv, CryptoUtils.KeySize));
        var recv = ChainKey.FromBytes(CryptoUtils.KDF(null, root.ToArray(), LabelInitSend, CryptoUtils.KeySize));
        return (send, recv);
    }

    public static SecureSession CreateInitiatorSession(
        SessionId id,
        PeerId remotePeerId,
        ProtocolVersion protocol,
        RootKey root,
        IClock clock,
        ISessionCrypto? crypto = null,
        int skippedKeyLimit = 1000)
    {
        var (send, recv) = DeriveInitiatorChains(root);
        var state = new RatchetState(root, send, 0, recv, 0, 0, null, null, skippedKeyLimit);
        crypto ??= new AeadSessionCrypto();
        return SecureSession.Create(id, remotePeerId, protocol, state, crypto, clock);
        
    }

    public static SecureSession CreateResponderSession(
        SessionId id,
        PeerId remotePeerId,
        ProtocolVersion protocol,
        RootKey root,
        IClock clock,
        ISessionCrypto? crypto = null,
        int skippedKeyLimit = 1000)
    {
        var (send, recv) = DeriveResponderChains(root);
        var state = new RatchetState(root, send, 0, recv, 0, 0, null, null, skippedKeyLimit);
        crypto ??= new AeadSessionCrypto();
        return SecureSession.Create(id, remotePeerId, protocol, state, crypto, clock);
    }
}
