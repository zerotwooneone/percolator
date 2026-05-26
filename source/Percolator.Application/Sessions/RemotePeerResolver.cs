using Microsoft.Extensions.Logging;
using Percolator.Application.Identity;
using Percolator.Chat;
using Percolator.Cryptography;
using Percolator.Identity;

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

    public async Task<PeerId> ResolveFromSession(SessionId sessionId)
    {
        var convId = new Percolator.Chat.ValueObjects.ConversationId(sessionId.Value);
        if (_activeIdentityContext.Identity is null)
            throw new InvalidOperationException("Identity context not loaded");

        var conversation = await _directConversationRepository.GetByIdAsync(convId, _activeIdentityContext.Identity.SelfIdentityId.Value, default).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Direct conversation with id {sessionId} not found");

        var localPeerId = _activeIdentityContext.Identity.Id;
        var remote = conversation.Peer1.Value == localPeerId ? conversation.Peer2 : conversation.Peer1;
        var remoteId = new PeerId(remote.Value);
        _logger.LogInformation("Resolved remote peer {RemotePeerId} from session {SessionId}", remoteId, sessionId);
        return remoteId;
    }
}
