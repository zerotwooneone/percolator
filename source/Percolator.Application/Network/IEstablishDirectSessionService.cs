using Percolator.Cryptography.Primitives;

namespace Percolator.Application.Network;

/// <summary>
/// Used to create a pending session in the reverse signal flow
/// </summary>
public interface IEstablishDirectSessionService
{
    Task<RequestCorrelationId> QueueInviteAsync(
        byte[] inviterIdentityKeySpki,
        byte[] payloadBytes,
        byte[] payloadSignatureBytes,
        CancellationToken cancellationToken);
}