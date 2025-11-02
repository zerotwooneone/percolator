using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Percolator.Application.Identity;
using Percolator.Chat;
using Percolator.Cryptography;
using Percolator.Identity;

namespace Percolator.Application.Sessions;

public class RemotePeerResolver : IRemotePeerResolver
{
    private readonly IConversationRepository _conversationRepository;
    private readonly ActiveIdentityContext _activeIdentityContext;
    private readonly ILogger<RemotePeerResolver> _logger;

    public RemotePeerResolver(
        IConversationRepository conversationRepository,
        ActiveIdentityContext activeIdentityContext,
        ILogger<RemotePeerResolver> logger)
    {
        _conversationRepository = conversationRepository;
        _activeIdentityContext = activeIdentityContext;
        _logger = logger;
    }

    public async Task<PeerId> ResolveFromSession(SessionId sessionId)
    {
        var convId = new Percolator.Chat.ValueObjects.ConversationId(sessionId.Value);
        if (_activeIdentityContext.Identity is null)
            throw new InvalidOperationException("Identity context not loaded");

        var conversation = await _conversationRepository.GetByIdAsync(convId, _activeIdentityContext.Identity.SelfIdentityId).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Conversation with id {sessionId} not found");

        if (conversation.Participants.Count != 2)
        {
            _logger.LogWarning("Conversation {ConversationId} has {Count} participants; direct message resolver expects exactly 2", conversation.Id, conversation.Participants.Count);
        }

        var localPeerId = _activeIdentityContext.Identity.Id;
        var remote = conversation.Participants.First(p => p.Value != localPeerId);
        var remoteId = new PeerId(remote.Value);
        _logger.LogInformation("Resolved remote peer {RemotePeerId} from session {SessionId}", remoteId, sessionId);
        return remoteId;
    }
}
