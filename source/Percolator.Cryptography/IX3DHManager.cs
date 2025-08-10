using System.Security.Cryptography;

namespace Percolator.Cryptography;

public interface IX3DHManager
{
    SharedSecret InitiateHandshake(X3dPreKeyBundle remoteBundle, ECDiffieHellman ephemeralKey, ECDiffieHellman identityAgreementKey);
    SharedSecret RespondToHandshake(RatchetIdentityKey remoteIdentityKey, RatchetEphemeralKey remoteEphemeralKey, PrivateAgreementKey identityAgreementKey, PrivatePreKey signedPreKey, PrivateOneTimeKey? oneTimePreKey);
    Signature SignPreKey(ECDsa identitySigningKey, PreKey signedPreKey);
    bool VerifySignature(RatchetIdentityKey identityKey, PreKey signedPreKey, Signature signature);
}
