using System.Security.Cryptography;

namespace Percolator.Cryptography;

public interface IX3DHManager
{
    SharedSecret InitiateHandshake(PreKeyBundle remoteBundle, ECDiffieHellman ephemeralKey, ECDiffieHellman identityAgreementKey);
    SharedSecret RespondToHandshake(RatchetIdentityKey remoteIdentityKey, RatchetEphemeralKey remoteEphemeralKey, PrivateKey identityAgreementKey, PrivateKey signedPreKey, PrivateKey? oneTimePreKey);
    Signature SignPreKey(ECDsa identitySigningKey, PreKey signedPreKey);
    bool VerifySignature(RatchetIdentityKey identityKey, PreKey signedPreKey, Signature signature);
}
