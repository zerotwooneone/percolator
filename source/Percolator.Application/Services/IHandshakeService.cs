using Percolator.Cryptography;
using Percolator.Cryptography.Primitives;

namespace Percolator.Application.Services;

public interface IHandshakeService
{
    Task<(SessionId SessionId, SessionRatchetMessage? InitialCipher)> InitiateStandardHandshakeAsync(
        PeerId peerId,
        Plaintext? initialMessage,
        CancellationToken cancellationToken = default);
}
