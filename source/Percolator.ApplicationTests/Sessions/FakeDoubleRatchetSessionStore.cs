using System.Collections.Concurrent;
using Percolator.Cryptography;

namespace Percolator.ApplicationTests.Sessions
{
    public class FakeDoubleRatchetSessionStore : IDoubleRatchetSessionStore
    {
        private readonly
            ConcurrentDictionary<(int selfIdentityId, SessionId sessionId),
                DoubleRatchetSession.DoubleRatchetSessionState> _sessions = new();

        public Task<DoubleRatchetSession.DoubleRatchetSessionState?> GetSessionStateAsync(SessionId sessionId,
            int selfIdentityId)
        {
            _sessions.TryGetValue((selfIdentityId, sessionId), out var sessionState);
            return Task.FromResult(sessionState);
        }

        public Task SetSessionStateAsync(SessionId sessionId,
            DoubleRatchetSession.DoubleRatchetSessionState sessionState, int selfIdentityId)
        {
            _sessions[(selfIdentityId, sessionId)] = sessionState;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<SessionId>> GetAllSessionIdsAsync(int selfIdentityId)
        {
            var result = _sessions.Keys
                .Where(k => k.selfIdentityId == selfIdentityId)
                .Select(k => k.sessionId)
                .Distinct()
                .ToList()
                .AsReadOnly();
            return Task.FromResult((IReadOnlyList<SessionId>) result);
        }

        public Task<DoubleRatchetSession.DoubleRatchetSessionState?> FindByRemoteRatchetKeyAsync(
            PreKey remoteRatchetKey, int selfIdentityId)
        {
            var sessionState = _sessions.Values
                .FirstOrDefault(s => s.TheirDhRatchetPublicKey != null &&
                                     s.TheirDhRatchetPublicKey.Value.SequenceEqual(remoteRatchetKey.Value));
            return Task.FromResult(sessionState);
        }
    }
}
