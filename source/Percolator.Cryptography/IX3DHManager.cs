using System.Security.Cryptography;

namespace Percolator.Cryptography;

public interface IX3DHManager
{
    SharedSecret InitiateHandshake(X3dPreKeyBundle remoteBundle, ECDiffieHellman ephemeralKey, ECDiffieHellman identitySigningKey);
    SharedSecret RespondToHandshake(RatchetIdentityKey remoteIdentityKey, RatchetEphemeralKey remoteEphemeralKey, RatchetIdentityKey selfIdentitySigningKey, PrivatePreKey selfPreKey, PrivateOneTimeKey? selfOneTimePreKey);
    Signature SignPreKey(ECDiffieHellman identitySigningKey, PreKey signedPreKey);
    bool VerifySignature(RatchetIdentityKey identityKey, PreKey signedPreKey, Signature signature);
}
