using Percolator.Chat.GroupLedger;
using Percolator.Cryptography;

namespace Percolator.Application.Chat;

public interface IRelayTransportClient
{
    Task<DeliveryCertificate> FetchCertificateAsync(
        string targetHost,
        int targetPort,
        string senderPublicIdentityId,
        Percolator.Identity.PublicIdentityId targetPublicIdentityId,
        DateTimeOffset timestamp,
        Signature signature,
        CancellationToken ct);
}
