using Percolator.Cryptography;
using CryptoPeerId = Percolator.Cryptography.Primitives.PeerId;

namespace Percolator.Application.Services;

public interface IHandshakeService
{
    Task<(SessionId SessionId, SessionRatchetMessage? InitialCipher)> InitiateStandardHandshakeAsync(
        CryptoPeerId peerId,
        Plaintext? initialMessage,
        CancellationToken cancellationToken = default);
}
