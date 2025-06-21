namespace Pecolator.Cryptography;

/// <summary>
/// Represents a message produced by a Double Ratchet session, containing the data needed for the recipient to decrypt it and advance their ratchet state.
/// </summary>
/// <param name="EphemeralPublicKey">The sender's ephemeral Diffie-Hellman public key for this message.</param>
/// <param name="CiphertextPayload">The encrypted payload, which internally contains the message header and the actual ciphertext.</param>
public record RatchetMessage(byte[] EphemeralPublicKey, byte[] CiphertextPayload);
