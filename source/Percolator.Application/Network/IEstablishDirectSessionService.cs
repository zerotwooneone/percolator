using Percolator.Cryptography.Primitives;
using Percolator.Identity;

namespace Percolator.Application.Network;

/// <summary>
/// Used to create a pending session in the reverse signal flow
/// </summary>
public interface IEstablishDirectSessionService
{
    Task<RequestCorrelationId> QueueInviteAsync(
        SelfId selfIdentityId,
        byte[] inviterIdentityKeySpki,
        byte[] payloadBytes,
        byte[] payloadSignatureBytes,
        bool isRelayed,
        Percolator.Identity.PeerId? relayHostPeerId,
        CancellationToken cancellationToken);
}