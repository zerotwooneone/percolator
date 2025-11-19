using System;
using System.Threading;
using System.Threading.Tasks;
using Percolator.Cryptography;
using Percolator.Cryptography.Primitives;

namespace Percolator.Application.Services;

public sealed class HandshakeService : IHandshakeService
{
    private sealed class SystemClock : IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    }

    public HandshakeService()
    {
    }

    public Task<(SessionId SessionId, SessionRatchetMessage? InitialCipher)> InitiateStandardHandshakeAsync(
        PeerId peerId,
        Plaintext? initialMessage,
        CancellationToken cancellationToken = default)
    {
        // Minimal Green: create a session and optionally produce the first cipher
        var clock = new SystemClock();
        var sessionId = SessionId.NewId();
        var proto = new ProtocolVersion(1);
        var state = new RatchetState(
            new RootKey(new byte[32]),
            sendingChainKey: null,
            sendingCounter: 0,
            receivingChainKey: null,
            receivingCounter: 0,
            previousChainLength: 0,
            remoteRatchetKey: null,
            dhRatchetPrivateKey: null,
            skippedKeyLimit: 1000);

        var session = SecureSession.Create(sessionId, peerId, proto, state, clock);

        SessionRatchetMessage? initial = null;
        if (initialMessage is not null)
        {
            initial = session.Encrypt(initialMessage, clock);
        }

        return Task.FromResult((session.Id, initial));
    }
}
