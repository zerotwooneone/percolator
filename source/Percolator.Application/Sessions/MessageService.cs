using Microsoft.Extensions.Logging;
using Percolator.Application.Identity;
using Percolator.Application.Network;
using Percolator.Chat;
using Percolator.Chat.ValueObjects;
using ChatParticipantId = Percolator.Chat.ValueObjects.ParticipantId;
using ChatConversationId = Percolator.Chat.ValueObjects.ConversationId;
using Percolator.Cryptography;
using IdentityPeerId = Percolator.Identity.PeerId;

namespace Percolator.Application.Sessions;

/// <summary>
/// Provides messaging services for the application, working directly with cryptography primitives.
/// </summary>
public class MessageService : IMessageService
{
    private readonly IDirectSessionManager _sessionManager;
    private readonly IMessageTransportService _transportService;
    private readonly IConversationRepository _conversationRepository;
    private readonly ILogger<MessageService> _logger;
    private readonly ActiveIdentityContext _activeIdentityContext;
    private readonly IDoubleRatchetSessionStore _sessionStore;

    public MessageService(
        IDirectSessionManager sessionManager,
        IMessageTransportService transportService,
        IConversationRepository conversationRepository,
        ILogger<MessageService> logger,
        ActiveIdentityContext activeIdentityContext,
        IDoubleRatchetSessionStore sessionStore)
    {
        _sessionManager = sessionManager;
        _transportService = transportService;
        _conversationRepository = conversationRepository;
        _logger = logger;
        _activeIdentityContext = activeIdentityContext;
        _sessionStore = sessionStore;
    }

    public async Task SendDirectMessageAsync(
        ChatConversationId conversationId, 
        string content)
    {
        if (_activeIdentityContext.Identity is null)
        {
            throw new InvalidOperationException("Identity context not loaded");
        }
        var sessionId = new SessionId(conversationId.Value);
        var sessionState = await _sessionStore.GetSessionStateAsync(sessionId);
        if (sessionState == null)
        {
            throw new InvalidOperationException($"Double Ratchet session state for conversation {conversationId} not found.");
        }
        _logger.LogInformation("Sending message to conversation {ConversationId}", conversationId);
       
        // Convert content to plaintext bytes
        var plaintext = new Plaintext(System.Text.Encoding.UTF8.GetBytes(content));

        // Encrypt message using Double Ratchet
        var encryptResult = await _sessionManager.EncryptMessageAsync(new SessionId(conversationId.Value), plaintext);
        if (encryptResult == null)
        {
            throw new InvalidOperationException($"Failed to encrypt message. Conversation {conversationId} not found.");
        }

        var (remotePeerId, encryptedMessage) = encryptResult.Value;
        var identityRemotePeerId = new IdentityPeerId(remotePeerId.Value);

        // Send encrypted message using gRPC
        await _transportService.SendMessageAsync(
            identityRemotePeerId,
            conversationId,
            encryptedMessage);
        
        // Record message in local conversation
        var senderParticipantId = new ChatParticipantId(_activeIdentityContext.Identity.Id);
        var remoteParticipantId = new ChatParticipantId(remotePeerId.Value);
        
        // Get or create conversation
        var conversation = await _conversationRepository.GetByIdAsync(conversationId);
        if (conversation == null)
        {
            // Create a new conversation if it doesn't exist
            _logger.LogInformation("Creating new conversation {ConversationId}", conversationId);
            conversation = new Conversation(
                conversationId,
                new ChannelId(sessionState.TheirIdentityPublicKey.Value), // Generate a new channel ID for this conversation
                new List<ChatParticipantId> { senderParticipantId, remoteParticipantId },
                new List<Message>(),
                null // No name for direct conversations
            );
            
            await _conversationRepository.AddAsync(conversation);
        }
        
        // Add the message to the conversation
        conversation.AddMessage(senderParticipantId, content);
        await _conversationRepository.UpdateAsync(conversation);
    }
}
