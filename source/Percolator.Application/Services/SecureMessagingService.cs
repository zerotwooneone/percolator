using Percolator.Cryptography;

namespace Percolator.Application.Services;

public sealed class SecureMessagingService : ISecureMessagingService
{
    private readonly ISessionRepository _sessions;
    private readonly IRatchetKeyIndex _index;
    private readonly ISessionCatalog _catalog;
    private readonly IClock _clock;

    public SecureMessagingService(
        ISessionRepository sessions, 
        IRatchetKeyIndex index, 
        ISessionCatalog catalog,
        IClock clock)
    {
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _index = index ?? throw new ArgumentNullException(nameof(index));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _clock = clock;
    }

    public async Task<SessionRatchetMessage> EncryptAsync(SessionId sessionId, Plaintext plaintext, CancellationToken cancellationToken = default)
    {
        var s = await _sessions.GetAsync(sessionId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Session {sessionId.Value} not found");
        var msg = s.Encrypt(plaintext, _clock);
        await _sessions.UpdateAsync(s, cancellationToken).ConfigureAwait(false);
        return msg;
    }

    public async Task<(SessionId sessionId, Plaintext plaintext)?> DecryptInboundAsync(int selfIdentityId, SessionRatchetMessage message, CancellationToken cancellationToken = default)
    {
        var resolver = new InboundMessageResolver(_index, _catalog, _sessions);
        var result = await resolver.ResolveAsync(selfIdentityId, message, _clock, cancellationToken).ConfigureAwait(false);
        return result;
    }
}
