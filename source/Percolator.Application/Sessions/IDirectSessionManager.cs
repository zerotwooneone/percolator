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
    Task EstablishSessionAsInitiatorAsync(SessionId sessionId,
        RatchetIdentityKey remoteIdentityKey,
        PreKey preKey,
        SharedSecret sharedSecret, ECDiffieHellman localEphemeralKey);

    /// <summary>
    /// Establishes a new Double Ratchet session as the responder.
    /// </summary>
    Task EstablishSessionAsResponderAsync(
        Percolator.Cryptography.SessionId sessionId, 
        RatchetIdentityKey remoteIdentityKey,
        PreKey remotePreKey,
        ECDiffieHellman privateKeyUsedInHandshake,
        SharedSecret sharedSecret);

    /// <summary>
    /// Receives and decrypts an incoming message.
    /// </summary>
    Task<Plaintext?> ReceiveMessageAsync(Percolator.Cryptography.SessionId conversationId, SessionRatchetMessage encryptedMessage);

    /// <summary>
    /// Encrypts an outgoing message.
    /// </summary>
    Task<SessionRatchetMessage> EncryptMessageAsync(Percolator.Cryptography.SessionId sessionId, Plaintext plaintext);

    /// <summary>
    /// Slow path: attempts to infer the correct session by trial decrypt when fast header-key lookup misses.
    /// Returns the matched session id and plaintext if a match is found; otherwise null.
    /// </summary>
    Task<(Percolator.Cryptography.SessionId sessionId, Plaintext? plaintext)?> TryInferAndReceiveAsync(SessionRatchetMessage encryptedMessage, CancellationToken cancellationToken);
    
    /// <summary>
    /// Completes a pre-handshake session establishment by decrypting the responder hello and extracting the session id from the plaintext.
    /// The session is created, stored, and the matched prehandshake record is removed.
    /// </summary>
    Task<(Percolator.Cryptography.SessionId sessionId, Plaintext plaintext)> CompleteHandshakeAsync(SessionRatchetMessage encryptedMessage, Func<Plaintext, SessionId> getSessionId, CancellationToken cancellationToken);

    /// <summary>
    /// Persists an initiator intent (pre-handshake Pending record) without a session id.
    /// Stores minimal material required to complete handshake on responder hello.
    /// </summary>
    Task<SessionRatchetMessage?> EstablishSessionAsInitiatorAsync(
        byte[] recipientPublicKeyHash,
        Guid signedPreKeyId,
        Guid? oneTimePreKeyId,
        RatchetIdentityKey remoteIdentityKey,
        PreKey remotePreKey,
        SharedSecret sharedSecret,
        ECDiffieHellman initiatorEphemeral,
        Plaintext? initialPlaintext,
        CancellationToken cancellationToken);
}
