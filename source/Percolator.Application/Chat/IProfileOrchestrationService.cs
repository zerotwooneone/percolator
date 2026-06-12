using Percolator.Contracts;
using Percolator.Identity;

namespace Percolator.Application.Chat;

public interface IProfileOrchestrationService
{
    Task UpdateLocalProfileAsync(string newDisplayName, CancellationToken ct);
    Task AttachProfileDataIfRequiredAsync(ChatEnvelope envelope, PeerId recipientPeerId, CancellationToken ct);
    Task ProcessInboundProfileDataAsync(ChatEnvelope envelope, PeerId senderPeerId, CancellationToken ct);
}
