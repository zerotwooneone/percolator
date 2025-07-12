using System.Collections.Concurrent;
using Percolator.Sessions;

namespace Percolator.ApplicationTests.Sessions
{
    public class FakeDoubleRatchetSessionStore : IDoubleRatchetSessionStore
    {
        private readonly ConcurrentDictionary<string, SessionState> _sessions = new();

        public Task<SessionState?> GetSessionStateAsync(string sessionId)
        {
            _sessions.TryGetValue(sessionId, out var sessionState);
            return Task.FromResult(sessionState);
        }

        public Task SetSessionStateAsync(string sessionId, SessionState sessionState)
        {
            _sessions[sessionId] = sessionState;
            return Task.CompletedTask;
        }
    }
}
