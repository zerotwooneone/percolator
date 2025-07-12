using Percolator.Sessions;
using SessionConversationId = Percolator.Sessions.ConversationId;
using SessionPeerId = Percolator.Sessions.PeerId;

namespace Percolator.Application.Sessions;

/// <summary>
/// Manages the lifecycle of direct, end-to-end encrypted sessions between two peers.
/// </summary>
public interface IDirectSessionManager
{
    /// <summary>
    /// Establishes a new Double Ratchet session as the initiator.
    /// </summary>
    Task EstablishSessionAsInitiatorAsync(SessionConversationId conversationId, SessionPeerId remotePeerId, SessionIdentityKey remoteIdentityKey, SessionRatchetKey remoteRatchetKey, SharedSecret sharedSecret);

    /// <summary>
    /// Establishes a new Double Ratchet session as the responder.
    /// </summary>
    Task EstablishSessionAsResponderAsync(SessionConversationId conversationId, SessionPeerId remotePeerId, SessionIdentityKey remoteIdentityKey, SharedSecret sharedSecret);

    /// <summary>
    /// Receives and decrypts an incoming message.
    /// </summary>
    Task<Plaintext?> ReceiveMessageAsync(SessionConversationId conversationId, RatchetMessage encryptedMessage);

    /// <summary>
    /// Encrypts an outgoing message.
    /// </summary>
    Task<(SessionPeerId remotePeerId, RatchetMessage encryptedMessage)?> EncryptMessageAsync(SessionConversationId conversationId, Plaintext plaintext);
}
