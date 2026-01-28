using System.Threading;
using System.Threading.Tasks;
using Percolator.Cryptography;
using Percolator.Contracts;

namespace Percolator.Application.Network.Handshake
{
    public interface IInitiatorFinalizeService
    {
        Task<(SessionId sessionId, Plaintext plaintext)?> TryFinalizeFromFirstResponderAsync(
            SessionRatchetMessage responderFirst,
            CancellationToken cancellationToken = default);

        Task<(SessionId sessionId, Plaintext plaintext)?> TryFinalizeFromInviteHandshakeResponseAsync(
            InviteHandshakeResponse response,
            CancellationToken cancellationToken = default);
    }
}
