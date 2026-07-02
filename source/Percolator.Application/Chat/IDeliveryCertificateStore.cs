using Percolator.Chat.GroupLedger;
using Percolator.Chat.GroupMembership;

namespace Percolator.Application.Chat;

public interface IDeliveryCertificateStore
{
    DeliveryCertificate? GetCertificate();
    void SetCertificate(ChatPeerId relayPeerId,DeliveryCertificate certificate);
}
