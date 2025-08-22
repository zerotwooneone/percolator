using System.Security.Cryptography;
using Percolator.Cryptography;
using Percolator.Identity;

namespace Percolator.Application.Sessions;

/// <summary>
/// Manages the lifecycle of direct, end-to-end encrypted sessions between two peers.
/// </summary>
public interface IDirectSessionManager
{
    /// <summary>
    /// Establishes a new Double Ratchet session as the initiator.
    /// </summary>
    Task EstablishSessionAsInitiatorAsync(SessionId conversationId,
        PeerId remotePeerId,
        RatchetIdentityKey remoteIdentityKey,
        RatchetEphemeralKey remoteRatchetKey,
        SharedSecret sharedSecret, ECDiffieHellman localEphemeralKey);

    /// <summary>
    /// Establishes a new Double Ratchet session as the responder.
    /// </summary>
    Task EstablishSessionAsResponderAsync(
        Percolator.Cryptography.SessionId conversationId, 
        Percolator.Identity.PeerId remotePeerId, 
        RatchetIdentityKey remoteIdentityKey,
        RatchetEphemeralKey remoteRatchetPublicKey,
        ECDiffieHellman privateKeyUsedInHandshake,
        SharedSecret sharedSecret);

    /// <summary>
    /// Receives and decrypts an incoming message.
    /// </summary>
    Task<Plaintext?> ReceiveMessageAsync(Percolator.Cryptography.SessionId conversationId, SessionRatchetMessage encryptedMessage);

    /// <summary>
    /// Encrypts an outgoing message.
    /// </summary>
    Task<(Percolator.Identity.PeerId remotePeerId, SessionRatchetMessage encryptedMessage)?> EncryptMessageAsync(Percolator.Cryptography.SessionId conversationId, Plaintext plaintext);

    Task<PeerId> GetRemotePeerIdFromDirectMessage(SessionId sessionId);
}
