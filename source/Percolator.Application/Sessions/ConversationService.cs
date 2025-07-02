using System;
using System.Threading;
using System.Threading.Tasks;
using Percolator.Application.Identity;
using Percolator.Identity;
using Percolator.Sessions;
using IdentityPeerId = Percolator.Identity.PeerId;
using SessionPeerId = Percolator.Sessions.PeerId;

namespace Percolator.Application.Sessions;

public class ConversationService : IConversationService
{
    private readonly IMessageStore _messageStore;
    private readonly IPeerRepository _peerRepository;
    private readonly ActiveIdentityContext _activeIdentityContext;

    public ConversationService(IMessageStore messageStore, IPeerRepository peerRepository, ActiveIdentityContext activeIdentityContext)
    {
        _messageStore = messageStore;
        _peerRepository = peerRepository;
        _activeIdentityContext = activeIdentityContext;
    }

    public async Task<ConversationId> CreateDirectConversationAsync(IdentityPeerId peerId, CancellationToken cancellationToken)
    {
        // Check if peer exists
        if (await _peerRepository.GetByIdAsync(peerId) is null)
        {
            throw new ArgumentException("Peer not found.", nameof(peerId));
        }

        var sessionPeerId = new SessionPeerId(peerId.Value);

        // Check if conversation already exists
        var conversation = await _messageStore.GetConversationWithPeerAsync(sessionPeerId, cancellationToken);
        if (conversation is not null)
        {
            return conversation.Id;
        }

        // Create and store new conversation
        if (_activeIdentityContext.Identity is null)
        {
            throw new InvalidOperationException("No active identity found to create conversation.");
        }

        var localPeerId = new Percolator.Sessions.PeerId(_activeIdentityContext.Identity.Id);
        var newConversation = new DirectConversation(ConversationId.NewId(), localPeerId, sessionPeerId);
        await _messageStore.StoreDirectConversationAsync(newConversation);
        return newConversation.Id;
    }
}
