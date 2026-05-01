using Percolator.Cryptography;
using Percolator.Cryptography.Primitives;

namespace Percolator.Application.Services;

public sealed class HandshakeService : IHandshakeService
{
    private readonly IClock _clock;

    public HandshakeService(IClock clock)
    {
        _clock = clock;
    }

    public Task<(SessionId SessionId, SessionRatchetMessage? InitialCipher)> InitiateStandardHandshakeAsync(
        PeerId peerId,
        Plaintext? initialMessage,
        CancellationToken cancellationToken = default)
    {
        // Minimal Green: create a session and optionally produce the first cipher
        // critical: this is not secure - the root key is zero
        var sessionId = SessionId.NewId();
        var proto = new ProtocolVersion(1);
        var root = RootKey.FromBytesOwned(new byte[32]);
        var session = RatchetBootstrap.CreateInitiatorSession(sessionId, peerId, proto, root, _clock);

        SessionRatchetMessage? initial = null;
        if (initialMessage is not null)
        {
            initial = session.Encrypt(initialMessage, _clock);
        }

        return Task.FromResult((session.Id, initial));
    }
}

