using Percolator.Cryptography;
using Percolator.Identity;

namespace Percolator.Application.Chat;

public interface IPeerAuthenticationService
{
    Task<bool> AuthenticateDeliveryCertificateRequestAsync(
        PublicIdentityId senderPublicIdentityId,
        DateTimeOffset requestTimestamp,
        Signature signature,
        CancellationToken ct);
}
