using System.Threading.Tasks;
using Percolator.Cryptography;
using Percolator.Sessions;

namespace Percolator.Application.Network
{
    public interface IMessageTransportService
    {
        Task SendMessageAsync(PeerId recipientPeerId, ConversationId conversationId, RatchetMessage message);
        // This interface will also need a way to receive messages, perhaps via an event or a callback mechanism
        // For now, we'll focus on sending, and the HostedService will handle receiving.
    }
}