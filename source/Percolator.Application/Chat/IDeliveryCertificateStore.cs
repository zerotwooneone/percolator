using Percolator.Chat.GroupLedger;

namespace Percolator.Application.Chat;

public interface IDeliveryCertificateStore
{
    DeliveryCertificate? GetCertificate();
    void SetCertificate(DeliveryCertificate certificate);
}
