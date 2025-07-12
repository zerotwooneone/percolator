namespace Percolator.Sessions;

/// <summary>
/// Defines the abstract protocol for performing Double Ratchet cryptographic operations.
/// This interface decouples the session management logic from the underlying cryptographic implementation.
/// </summary>
public interface IDoubleRatchetProtocol
{
    /// <summary>
    /// Initiates a new Double Ratchet session as the initiator.
    /// </summary>
    /// <returns>The initial state of the session.</returns>
    (SessionState, RatchetEphemeralKey) InitiateSession(RatchetIdentityKey theirIdentityKey, RatchetEphemeralKey theirRatchetKey, SharedSecret sharedSecret);

    /// <summary>
    /// Responds to a new Double Ratchet session as the responder.
    /// </summary>
    /// <returns>The initial state of the session.</returns>
    SessionState RespondToSession(RatchetIdentityKey theirIdentityKey, PrivateEphemeralKey ourRatchetKey, SharedSecret sharedSecret);

    /// <summary>
    /// Encrypts a plaintext message using the current session state.
    /// </summary>
    /// <returns>A tuple containing the new session state and the encrypted message.</returns>
    (SessionState newState, RatchetMessage ciphertext) Encrypt(SessionState currentState, Plaintext plaintext);

    /// <summary>
    /// Decrypts a ratchet message using the current session state.
    /// </summary>
    /// <returns>A tuple containing the new session state and the decrypted plaintext.</returns>
    (SessionState newState, Plaintext? plaintext) Decrypt(SessionState currentState, RatchetMessage ciphertext);
}
