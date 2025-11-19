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
        RatchetEphemeralKey preKey,
        SharedSecret sharedSecret, ECDiffieHellman localEphemeralKey);

    /// <summary>
    /// Establishes a new Double Ratchet session as the responder.
    /// </summary>
    Task EstablishSessionAsResponderAsync(
        Percolator.Cryptography.SessionId sessionId, 
        RatchetIdentityKey remoteIdentityKey,
        RatchetEphemeralKey remotePreKey,
        ECDiffieHellman privateKeyUsedInHandshake,
        SharedSecret sharedSecret);

    /// <summary>
    /// Finalizes an initiator session using the Initial Root Key and the responder's first header ratchet key.
    /// Responder assigns the session id; initiator must not derive it.
    /// </summary>
    Task FinalizeAsInitiatorAsync(
        SessionId sessionId,
        RatchetIdentityKey responderIdentityKey,
        SharedSecret initialRootKey,
        RatchetEphemeralKey responderPublicRatchetKey);
}
