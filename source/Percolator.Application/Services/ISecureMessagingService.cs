using Percolator.Cryptography;

namespace Percolator.Application.Services;

public interface ISecureMessagingService
{
    Task<SessionRatchetMessage> EncryptAsync(SessionId sessionId, Plaintext plaintext, CancellationToken cancellationToken = default);
    Task<(SessionId sessionId, Plaintext plaintext)?> DecryptInboundAsync(int selfIdentityId, SessionRatchetMessage message, CancellationToken cancellationToken = default);
}
