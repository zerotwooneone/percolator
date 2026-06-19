using Percolator.Chat.GroupLedger;
using Percolator.Cryptography;

namespace Percolator.Application.Chat;

public interface IRelayTransportClient
{
    Task<DeliveryCertificate> FetchCertificateAsync(
        string targetHost,
        int targetPort,
        string senderPkh,
        DateTimeOffset timestamp,
        Signature signature,
        CancellationToken ct);
}
