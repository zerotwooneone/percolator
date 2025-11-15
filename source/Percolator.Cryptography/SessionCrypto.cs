namespace Percolator.Cryptography;

public interface ISessionCrypto
{
    // X3DH initiate: returns shared secret and our ephemeral public key to include in response
    (SharedSecret SharedSecret, RatchetEphemeralKey EphemeralPublic) X3DH_Initiate(
        PrivatePreKey localIdentityPrivate,
        PreKeyBundle remoteBundle);

    // Double Ratchet primitives (shape for future steps)
    (Ciphertext Ciphertext, RatchetState NewState) DR_Encrypt(RatchetState state, Plaintext pt, AssociatedData ad);
    (Plaintext Plaintext, RatchetState NewState) DR_Decrypt(RatchetState state, SessionRatchetMessage framed, AssociatedData ad);

    // Signature verification (bundle validation)
    bool VerifySignature(RatchetIdentityKey identityPublic, PreKey signedPreKey, Signature signature);
}
