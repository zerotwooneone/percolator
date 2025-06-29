using System;
using System.Threading.Tasks;
using Percolator.Identity;
using Percolator.Sessions;
using IdentityPeerId = Percolator.Identity.PeerId;
using SessionPeerId = Percolator.Sessions.PeerId;

namespace Percolator.Application.Sessions;

public class ConversationService : IConversationService
{
    private readonly IMessageStore _messageStore;
    private readonly IPeerRepository _peerRepository;

    public ConversationService(IMessageStore messageStore, IPeerRepository peerRepository)
    {
        _messageStore = messageStore;
        _peerRepository = peerRepository;
    }

    public async Task<ConversationId> CreateDirectConversationAsync(IdentityPeerId peerId)
    {
        // Check if peer exists
        if (await _peerRepository.GetByIdAsync(peerId.Value) is null)
        {
            throw new ArgumentException("Peer not found.", nameof(peerId));
        }

        var sessionPeerId = new SessionPeerId(peerId.Value);

        // Check if conversation already exists
        var conversation = await _messageStore.GetDirectConversationByPeerIdAsync(sessionPeerId);
        if (conversation is not null)
        {
            return conversation.Id;
        }

        // Create and store new conversation
        var newConversation = new DirectConversation(new ConversationId(Guid.NewGuid()), sessionPeerId);
        await _messageStore.StoreDirectConversationAsync(newConversation);
        return newConversation.Id;
    }
}
