using Percolator.Contracts;
using IdentityPeerId = Percolator.Identity.PeerId;

namespace Percolator.Application.Chat;

public interface IProfileOrchestrationService
{
    Task UpdateLocalProfileAsync(string newDisplayName, CancellationToken ct);
    Task AttachProfileDataIfRequiredAsync(ChatEnvelope envelope, IdentityPeerId recipientPeerId, CancellationToken ct);
    Task ProcessInboundProfileDataAsync(ChatEnvelope envelope, IdentityPeerId senderPeerId, CancellationToken ct);
}
