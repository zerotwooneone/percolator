using Percolator.Cryptography;

namespace Percolator.Application.Chat;

public interface IPeerAuthenticationService
{
    Task<bool> AuthenticateDeliveryCertificateRequestAsync(
        string senderPkh,
        DateTimeOffset requestTimestamp,
        Signature signature,
        CancellationToken ct);
}
