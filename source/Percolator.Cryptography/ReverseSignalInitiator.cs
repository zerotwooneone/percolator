namespace Percolator.Cryptography;

public interface IReverseSignalInitiator
{
    (HandshakeInvitation Invitation, InitiatorResult Result) CreateInvitation(
        RatchetIdentityKey localIdentityKey,
        PreKeyBundle remoteBundle);

    SessionRatchetMessage CreateInitialMessage(InitiatorResult initiatorResult);
}

public sealed class ReverseSignalInitiator : IReverseSignalInitiator
{
    private readonly IX3dhDeriver _deriver;
    private readonly IKeyStore _keyStore;
    private readonly IHandshakeInvitationParser _parser;
    private readonly IRatchetEngine _ratchet;

    public ReverseSignalInitiator(IX3dhDeriver deriver, IKeyStore keyStore, IHandshakeInvitationParser parser, IRatchetEngine ratchet)
    {
        _deriver = deriver ?? throw new ArgumentNullException(nameof(deriver));
        _keyStore = keyStore ?? throw new ArgumentNullException(nameof(keyStore));
        _parser = parser ?? throw new ArgumentNullException(nameof(parser));
        _ratchet = ratchet ?? throw new ArgumentNullException(nameof(ratchet));
    }

    public (HandshakeInvitation Invitation, InitiatorResult Result) CreateInvitation(
        RatchetIdentityKey localIdentityKey,
        PreKeyBundle remoteBundle)
    {
        if (localIdentityKey is null) throw new ArgumentNullException(nameof(localIdentityKey));
        if (remoteBundle is null) throw new ArgumentNullException(nameof(remoteBundle));

        var idPriv = _keyStore.GetIdentityPrivateKey();

        var init = _deriver.DeriveInitiator(
            remoteBundle.IdentitySigningKey,
            remoteBundle.SignedPreKey,
            remoteBundle.OneTimePreKey,
            idPriv);

        var spkId = remoteBundle.SignedPreKeyId.ToString();
        var opkId = remoteBundle.OneTimePreKeyId?.ToString();

        var invitation = HandshakeInvitationBuilder.Build(
            localIdentityKey,
            init.InitiatorEphemeralPublicKey,
            spkId,
            opkId);

        return (invitation, init);
    }

    public SessionRatchetMessage CreateInitialMessage(InitiatorResult initiatorResult)
    {
        if (initiatorResult is null) throw new ArgumentNullException(nameof(initiatorResult));

        var rootKey = new RootKey(initiatorResult.InitialRootKey.Value);
        var (sendChain, recvChain) = RatchetBootstrap.DeriveInitiatorChains(rootKey);

        var state = new RatchetState(
            rootKey,
            sendChain,
            sendingCounter: 0,
            recvChain,
            receivingCounter: 0,
            previousChainLength: 0,
            remoteRatchetKey: null,
            dhRatchetPrivateKey: null,
            skippedKeyLimit: 1000);

        var pt = new Plaintext(Array.Empty<byte>());
        var ad = new AssociatedData(Array.Empty<byte>());

        var (ciphertext, headerKey, _) = _ratchet.Encrypt(state, pt, ad, counter: 0, previousChainLength: 0);
        return SessionRatchetMessage.Create(headerKey, 0, 0, ciphertext);
    }
}
