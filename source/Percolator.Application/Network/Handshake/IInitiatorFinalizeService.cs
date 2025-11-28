using System.Threading;
using System.Threading.Tasks;
using Percolator.Cryptography;

namespace Percolator.Application.Network.Handshake
{
    public interface IInitiatorFinalizeService
    {
        Task<(SessionId sessionId, Plaintext plaintext)?> TryFinalizeFromFirstResponderAsync(
            SessionRatchetMessage responderFirst,
            CancellationToken cancellationToken = default);
    }
}
