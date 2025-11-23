namespace Percolator.Cryptography;

public interface IX3dhDeriver
{
    InitiatorResult DeriveInitiator(
        RatchetIdentityKey remoteIdentityKey,
        PreKey remoteSignedPreKey,
        OneTimeKey? remoteOneTimePreKey,
        PrivatePreKey localIdentityPrivateKey);

    ResponderResult DeriveResponder(
        RatchetIdentityKey initiatorIdentityKey,
        RatchetEphemeralKey initiatorEphemeralKey,
        PrivatePreKey localIdentityPrivateKey,
        PrivatePreKey localSignedPreKeyPrivate,
        PrivatePreKey? localOneTimePreKeyPrivate);
}

public sealed record InitiatorResult(
    SharedSecret InitialRootKey,
    RatchetEphemeralKey InitiatorEphemeralPublicKey,
    PrivatePreKey InitiatorEphemeralPrivateKey,
    bool UsedOneTimeKey);

public sealed record ResponderResult(
    SharedSecret InitialRootKey,
    bool UsedOneTimeKey);
