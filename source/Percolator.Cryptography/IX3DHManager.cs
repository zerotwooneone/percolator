using System.Security.Cryptography;

namespace Percolator.Cryptography;

public interface IX3DHManager
{
    SharedSecret InitiateHandshake(PreKeyBundle remoteBundle, ECDiffieHellman ephemeralKey, ECDiffieHellman identityAgreementKey);
    SharedSecret RespondToHandshake(PublicKey remoteIdentityKey, PublicKey remoteEphemeralKey, ECDsa identitySigningKey, ECDiffieHellman identityAgreementKey, ECDiffieHellman signedPreKey, ECDiffieHellman? oneTimePreKey);
    Signature SignPreKey(ECDsa identitySigningKey, PublicKey signedPreKey);
    bool VerifySignature(PublicKey identityKey, PublicKey signedPreKey, Signature signature);
}
