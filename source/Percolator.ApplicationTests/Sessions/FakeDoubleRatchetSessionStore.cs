using System.Collections.Concurrent;
using Percolator.Cryptography;

namespace Percolator.ApplicationTests.Sessions
{
    public class FakeDoubleRatchetSessionStore : IDoubleRatchetSessionStore
    {
        private readonly ConcurrentDictionary<(int selfIdentityId, SessionId sessionId), DoubleRatchetSession.DoubleRatchetSessionState> _sessions = new();

        public Task<DoubleRatchetSession.DoubleRatchetSessionState?> GetSessionStateAsync(SessionId sessionId, int selfIdentityId)
        {
            _sessions.TryGetValue((selfIdentityId, sessionId), out var sessionState);
            return Task.FromResult(sessionState);
        }

        public Task SetSessionStateAsync(SessionId sessionId, DoubleRatchetSession.DoubleRatchetSessionState sessionState, int selfIdentityId)
        {
            _sessions[(selfIdentityId, sessionId)] = sessionState;
            return Task.CompletedTask;
        }
    }
}
