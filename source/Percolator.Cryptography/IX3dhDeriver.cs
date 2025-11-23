namespace Percolator.Cryptography;

public interface IX3dhDeriver
{
    (SharedSecret irk, RatchetEphemeralKey initiatorEphemeralPublic) DeriveInitiator(
        RatchetIdentityKey remoteIk,
        PreKey remoteSpk,
        OneTimeKey? remoteOtk,
        PrivatePreKey localIkPriv);

    SharedSecret DeriveResponder(
        RatchetIdentityKey initiatorIk,
        RatchetEphemeralKey initiatorEk,
        PrivatePreKey localIkPriv,
        PrivatePreKey localSpkPriv,
        PrivatePreKey? localOtkPriv);
}
