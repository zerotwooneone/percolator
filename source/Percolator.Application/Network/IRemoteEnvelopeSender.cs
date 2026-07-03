using Percolator.Contracts;
using Percolator.Identity;

namespace Percolator.Application.Network
{
    public interface IRemoteEnvelopeSender
    {
        Task SendChatEnvelopeToPeerAsync(ChatEnvelope chatEnvelope, PeerId recipientPeerId, CancellationToken ct = default);
        
    }
}
