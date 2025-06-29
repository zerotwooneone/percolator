using System.Threading.Tasks;
using Percolator.Cryptography;
using Percolator.Sessions;


namespace Percolator.Application.Sessions
{
    public interface IDoubleRatchetSessionStore
    {
        Task SaveSessionStateAsync(PeerId peerId, ConversationId conversationId, Percolator.Cryptography.DoubleRatchetSession.DoubleRatchetSessionState sessionState);
        Task<Percolator.Cryptography.DoubleRatchetSession.DoubleRatchetSessionState?> GetSessionStateAsync(PeerId peerId, ConversationId conversationId);
        Task DeleteSessionStateAsync(PeerId peerId, ConversationId conversationId);
    }
}