using Percolator.Chat;

namespace Percolator.Application.Chat;

public interface IDeliveryCertificateStore
{
    DeliveryCertificate? GetCertificate();
    void SetCertificate(DeliveryCertificate certificate);
}
