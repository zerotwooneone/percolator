using System.Security.Cryptography;

namespace Percolator.Cryptography;

public interface IX3DHManager
{
    SharedSecret InitiateHandshake(X3dPreKeyBundle remoteBundle, ECDiffieHellman ephemeralKey, ECDiffieHellman identitySigningKey);
    SharedSecret RespondToHandshake(RatchetIdentityKey remoteIdentityKey, RatchetEphemeralKey remoteEphemeralKey, RatchetIdentityKey identityAgreementKey, PrivatePreKey signedPreKey, PrivateOneTimeKey? oneTimePreKey);
    Signature SignPreKey(ECDiffieHellman identitySigningKey, PreKey signedPreKey);
    bool VerifySignature(RatchetIdentityKey identityKey, PreKey signedPreKey, Signature signature);
}
