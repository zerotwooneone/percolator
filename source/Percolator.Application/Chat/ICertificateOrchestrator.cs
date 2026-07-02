using Percolator.Chat.GroupMembership;

namespace Percolator.Application.Chat;

public interface ICertificateOrchestrator
{
    Task RefreshLocalCertificateAsync(ChatPeerId relayPeerId,CancellationToken ct);
}
