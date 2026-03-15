namespace Percolator.Cryptography;

public interface IX3dhResponderBridge
{
    ResponderResult DeriveResponder(ParsedInvitation invitation);
}

public sealed class X3dhResponderBridge : IX3dhResponderBridge
{
    private readonly IX3dhDeriver _deriver;
    private readonly IKeyStore _keyStore;

    public X3dhResponderBridge(IX3dhDeriver deriver, IKeyStore keyStore)
    {
        _deriver = deriver ?? throw new ArgumentNullException(nameof(deriver));
        _keyStore = keyStore ?? throw new ArgumentNullException(nameof(keyStore));
    }

    public ResponderResult DeriveResponder(ParsedInvitation invitation)
    {
        if (invitation is null) throw new ArgumentNullException(nameof(invitation));

        var identityPriv = _keyStore.GetIdentityPrivateKey();
        var signedPreKeyPriv = _keyStore.GetSignedPreKeyPrivate(invitation.SignedPreKeyId);

        PrivatePreKey? oneTimePreKeyPriv = null;
        if (!string.IsNullOrWhiteSpace(invitation.OneTimePreKeyId))
        {
            oneTimePreKeyPriv = _keyStore.TryGetOneTimePreKeyPrivate(invitation.OneTimePreKeyId);
        }

        return _deriver.DeriveResponder(
            invitation.InitiatorIdentityKey,
            invitation.InitiatorEphemeralKey,
            identityPriv,
            signedPreKeyPriv,
            oneTimePreKeyPriv);
    }
}
