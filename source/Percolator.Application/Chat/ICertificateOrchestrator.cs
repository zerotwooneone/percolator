using Percolator.Chat.GroupMembership;

namespace Percolator.Application.Chat;

public interface ICertificateOrchestrator
{
    Task RefreshLocalCertificateAsync(ChatSelfId selfId, ChatPeerId relayPeerId, CancellationToken ct);
}
