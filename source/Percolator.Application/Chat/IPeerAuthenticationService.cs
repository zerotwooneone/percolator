using Percolator.Chat.Messaging.ValueObjects;
using Percolator.Cryptography;

namespace Percolator.Application.Chat;

public interface IPeerAuthenticationService
{
    Task<bool> AuthenticateDeliveryCertificateRequestAsync(
        Pkh senderPkh,
        DateTimeOffset requestTimestamp,
        Signature signature,
        CancellationToken ct);
}
