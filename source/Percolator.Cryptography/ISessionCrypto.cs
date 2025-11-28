namespace Percolator.Cryptography;

public interface ISessionCrypto
{
    // X3DH initiate: returns shared secret and our ephemeral public key to include in response
    (SharedSecret SharedSecret, RatchetEphemeralKey EphemeralPublic) X3DH_Initiate(
        PrivatePreKey localIdentityPrivate,
        PreKeyBundle remoteBundle);

    // X3DH responder: derive shared secret given initiator identity + ephemeral and our private material
    SharedSecret X3DH_Respond(
        RatchetIdentityKey initiatorId,
        RatchetEphemeralKey initiatorEph,
        PrivatePreKey localIdentityPrivate,
        PrivatePreKey localSpkPrivate,
        PrivatePreKey? localOtkPrivate);

    // Double Ratchet primitives: encryption must bind to header via AD and use counter for nonce
    (Ciphertext Ciphertext, RatchetEphemeralKey HeaderKey, RatchetState NewState) DR_Encrypt(
        RatchetState state,
        Plaintext pt,
        AssociatedData ad,
        ulong counter,
        ulong previousChainLength);
    (Plaintext Plaintext, RatchetState NewState) DR_Decrypt(RatchetState state, SessionRatchetMessage framed, AssociatedData ad);

    // Signature verification (bundle validation)
    bool VerifySignature(RatchetIdentityKey identityPublic, PreKey signedPreKey, Signature signature);
}
