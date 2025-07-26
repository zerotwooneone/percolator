using System.Collections.Concurrent;
using Percolator.Cryptography;

namespace Percolator.ApplicationTests.Sessions
{
    public class FakeDoubleRatchetSessionStore : IDoubleRatchetSessionStore
    {
        private readonly ConcurrentDictionary<SessionId, DoubleRatchetSession.DoubleRatchetSessionState> _sessions = new();

        public Task<DoubleRatchetSession.DoubleRatchetSessionState?> GetSessionStateAsync(SessionId sessionId)
        {
            _sessions.TryGetValue(sessionId, out var sessionState);
            return Task.FromResult(sessionState);
        }

        public Task SetSessionStateAsync(SessionId sessionId, DoubleRatchetSession.DoubleRatchetSessionState sessionState)
        {
            _sessions[sessionId] = sessionState;
            return Task.CompletedTask;
        }
    }
}
