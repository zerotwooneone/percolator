using System;
using System.Threading;
using System.Threading.Tasks;
using Percolator.Cryptography;
using Percolator.Cryptography.Primitives;

namespace Percolator.Application.Services;

public sealed class SecureMessagingService : ISecureMessagingService
{
    private readonly ISessionRepository _sessions;
    private readonly IRatchetKeyIndex _index;
    private readonly ISessionCatalog _catalog;

    private sealed class SystemClock : IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    }

    public SecureMessagingService(ISessionRepository sessions, IRatchetKeyIndex index, ISessionCatalog catalog)
    {
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _index = index ?? throw new ArgumentNullException(nameof(index));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    }

    public async Task<SessionRatchetMessage> EncryptAsync(SessionId sessionId, Plaintext plaintext, CancellationToken cancellationToken = default)
    {
        var s = await _sessions.GetAsync(sessionId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Session {sessionId.Value} not found");
        var clock = new SystemClock();
        var msg = s.Encrypt(plaintext, clock);
        await _sessions.UpdateAsync(s, cancellationToken).ConfigureAwait(false);
        return msg;
    }

    public async Task<(SessionId sessionId, Plaintext plaintext)?> DecryptInboundAsync(SessionRatchetMessage message, CancellationToken cancellationToken = default)
    {
        var clock = new SystemClock();
        var resolver = new InboundMessageResolver(_index, _catalog, _sessions);
        var result = await resolver.ResolveAsync(message, clock, cancellationToken).ConfigureAwait(false);
        return result;
    }
}
