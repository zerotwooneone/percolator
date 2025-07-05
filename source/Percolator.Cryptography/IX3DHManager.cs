using System.Security.Cryptography;

namespace Percolator.Cryptography;

public interface IX3DHManager
{
    SharedSecret InitiateHandshake(PreKeyBundle remoteBundle, ECDiffieHellman ephemeralKey, ECDiffieHellman identityAgreementKey);
    SharedSecret RespondToHandshake(byte[] remoteIdentityKeyBytes, byte[] remoteEphemeralKeyBytes, ECDsa identitySigningKey, ECDiffieHellman identityAgreementKey, ECDiffieHellman signedPreKey, ECDiffieHellman? oneTimePreKey);
    byte[] SignPreKey(ECDsa identitySigningKey, byte[] signedPreKey);
    bool VerifySignature(byte[] identityKey, byte[] signedPreKey, byte[] signature);
}
