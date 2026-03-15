using Percolator.Cryptography;
using Percolator.Contracts;
using Percolator.Identity;

namespace Percolator.Application.Network.Handshake
{
    public interface IInitiatorFinalizeService
    {
        Task<(SessionId sessionId, Plaintext plaintext)?> TryFinalizeFromFirstResponderAsync(
            SelfId selfIdentityId,
            SessionRatchetMessage responderFirst,
            CancellationToken cancellationToken = default);

        Task<(SessionId sessionId, Plaintext plaintext)?> TryFinalizeFromInviteHandshakeResponseAsync(
            SelfId selfIdentityId,
            InviteHandshakeResponse response,
            CancellationToken cancellationToken = default);
    }
}
