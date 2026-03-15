namespace Percolator.Cryptography;

public sealed class CryptoPrimitives : ICryptoPrimitives
{
    private readonly IHandshakeInvitationParser _parser;
    private readonly IX3dhResponderBridge _bridge;
    private readonly IRatchetEngine _ratchet;

    public CryptoPrimitives(
        IHandshakeInvitationParser parser,
        IX3dhResponderBridge bridge,
        IRatchetEngine ratchet)
    {
        _parser = parser ?? throw new ArgumentNullException(nameof(parser));
        _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
        _ratchet = ratchet ?? throw new ArgumentNullException(nameof(ratchet));
    }

    public HandshakeResponseMessage CreateHandshakeResponse(HandshakeInvitation invitation, IKeyStore keyStore)
    {
        if (invitation is null) throw new ArgumentNullException(nameof(invitation));
        if (keyStore is null) throw new ArgumentNullException(nameof(keyStore));

        var parsed = _parser.Parse(invitation);
        var responder = _bridge.DeriveResponder(parsed);
        
        // Convert shared secret from X3DH into initial RootKey
        var rootKey = new RootKey(responder.InitialRootKey.Value);

        // Derive responder chains from root key
        var (sendChain, recvChain) = RatchetBootstrap.DeriveResponderChains(rootKey);


        // Initialize minimal responder ratchet state for first outbound message.
        var initialState = new RatchetState(
            rootKey,
            sendChain,
            sendingCounter: 0,
            recvChain,
            receivingCounter: 0,
            previousChainLength: 0,
            remoteRatchetKey: null,
            dhRatchetPrivateKey: null,
            skippedKeyLimit: 1000);

        var emptyPt = new Plaintext(Array.Empty<byte>());
        var emptyAd = new AssociatedData(Array.Empty<byte>());

        var (ciphertext, headerKey, _) = _ratchet.Encrypt(
            initialState,
            emptyPt,
            emptyAd,
            counter: 0,
            previousChainLength: 0);

        var ratchetMessage = SessionRatchetMessage.Create(headerKey, 0, 0, ciphertext);
        return new HandshakeResponseMessage(ratchetMessage.Value);
    }
}
