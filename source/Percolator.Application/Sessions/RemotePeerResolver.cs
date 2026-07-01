using Microsoft.Extensions.Logging;
using Percolator.Application.Identity;
using Percolator.Chat;
using Percolator.Chat.GroupMembership;
using Percolator.Chat.Messaging.ValueObjects;
using Percolator.Cryptography;
using IdentityPeerId = Percolator.Identity.PeerId;

namespace Percolator.Application.Sessions;

public class RemotePeerResolver : IRemotePeerResolver
{
    private readonly IDirectConversationRepository _directConversationRepository;
    private readonly ActiveIdentityContext _activeIdentityContext;
    private readonly ILogger<RemotePeerResolver> _logger;

    public RemotePeerResolver(
        IDirectConversationRepository directConversationRepository,
        ActiveIdentityContext activeIdentityContext,
        ILogger<RemotePeerResolver> logger)
    {
        _directConversationRepository = directConversationRepository;
        _activeIdentityContext = activeIdentityContext;
        _logger = logger;
    }

    public async Task<IdentityPeerId> ResolveFromSession(SessionId sessionId)
    {
        var convId = new ConversationId(sessionId.Value);
        if (_activeIdentityContext.Identity is null)
            throw new InvalidOperationException("Identity context not loaded");

        var conversation = await _directConversationRepository.GetByIdAsync(convId, new ChatSelfId(_activeIdentityContext.Identity.SelfIdentityId.Value), default).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Direct conversation with id {sessionId} not found");

        var remoteId = new IdentityPeerId(conversation.Peer1.Value);
        _logger.LogInformation("Resolved remote peer {RemotePeerId} from session {SessionId}", remoteId, sessionId);
        return remoteId;
    }
}
