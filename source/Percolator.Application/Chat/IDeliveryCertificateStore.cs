using Percolator.Chat.GroupLedger;
using Percolator.Chat.GroupMembership;

namespace Percolator.Application.Chat;

public interface IDeliveryCertificateStore
{
    Task<DeliveryCertificate?> GetCertificateAsync(ChatSelfId selfId, ChatPeerId relayPeerId, CancellationToken ct);
    Task SetCertificateAsync(ChatSelfId selfId, ChatPeerId relayPeerId, DeliveryCertificate certificate, CancellationToken ct);
}
