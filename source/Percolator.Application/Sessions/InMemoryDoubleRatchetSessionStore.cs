using System.Collections.Concurrent;
using System.Threading.Tasks;
using Percolator.Cryptography;
using Percolator.Sessions;

namespace Percolator.Application.Sessions
{
    public class InMemoryDoubleRatchetSessionStore : IDoubleRatchetSessionStore
    {
        private readonly ConcurrentDictionary<(PeerId, ConversationId), DoubleRatchetSession.DoubleRatchetSessionState> _sessions = new();

        public Task SaveSessionStateAsync(PeerId peerId, ConversationId conversationId, DoubleRatchetSession.DoubleRatchetSessionState sessionState)
        {
            _sessions[(peerId, conversationId)] = sessionState;
            return Task.CompletedTask;
        }

        public Task<DoubleRatchetSession.DoubleRatchetSessionState?> GetSessionStateAsync(PeerId peerId, ConversationId conversationId)
        {
            _sessions.TryGetValue((peerId, conversationId), out var sessionState);
            return Task.FromResult(sessionState);
        }

        public Task DeleteSessionStateAsync(PeerId peerId, ConversationId conversationId)
        {
            _sessions.TryRemove((peerId, conversationId), out _);
            return Task.CompletedTask;
        }
    }
}