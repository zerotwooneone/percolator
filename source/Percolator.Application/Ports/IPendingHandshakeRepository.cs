using Percolator.Application.Ingress;
using Percolator.Domain.Identities;

namespace Percolator.Application.Ports;

/// <summary>
/// Infrastructure port for storing inbound handshake envelopes that are awaiting user contact approval.
/// Preserves piggybacked greeting messages and key material across application restarts.
/// </summary>
public interface IPendingHandshakeRepository
{
    Task SavePendingHandshakeAsync(PublicIdentityId recipientId, PublicIdentityId senderId, InboundHandshakeEnvelope envelope, CancellationToken ct = default);
    Task<InboundHandshakeEnvelope?> GetPendingHandshakeAsync(PublicIdentityId recipientId, PublicIdentityId senderId, CancellationToken ct = default);
    Task DeletePendingHandshakeAsync(PublicIdentityId recipientId, PublicIdentityId senderId, CancellationToken ct = default);
}
