using Percolator.Sessions;
using PreKeyBundle = Percolator.Contracts.PreKeyBundle;

namespace Percolator.Application.Sessions;

public record SessionResult(PreKeyBundle ResponderBundle, OpaquePublicKey InitiatorIdentityKey);

public interface ICryptoService
{
    /// <summary>
    /// Verifies the initiator's key bundle and prepares for session creation.
    /// </summary>
    /// <param name="initiatorBundle">The key bundle from the remote peer.</param>
    /// <returns>A result containing our bundle for the response and the verified public identity key of the initiator.</returns>
    Task<SessionResult> VerifyAndInitiateSessionAsync(PreKeyBundle initiatorBundle);

    /// <summary>
    /// Resets the secure session for an existing conversation.
    /// </summary>
    /// <param name="conversationId">The ID of the conversation to reset.</param>
    /// <param name="initiatorBundle">The new key bundle from the remote peer.</param>
    /// <returns>The local peer's new bundle to be sent in response.</returns>
    Task<PreKeyBundle> ResetSessionAsync(ConversationId conversationId, PreKeyBundle initiatorBundle);
}
