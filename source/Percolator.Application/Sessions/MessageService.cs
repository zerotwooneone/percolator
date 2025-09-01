using Microsoft.Extensions.Logging;
using Percolator.Application.Identity;
using Percolator.Application.Network;
using Percolator.Chat;
using Percolator.Chat.ValueObjects;
using ChatParticipantId = Percolator.Chat.ValueObjects.ParticipantId;
using ChatConversationId = Percolator.Chat.ValueObjects.ConversationId;
using Percolator.Cryptography;
using IdentityPeerId = Percolator.Identity.PeerId;
using Percolator.Contracts;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Percolator.Network;

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
    private readonly IDirectSessionRepository _directSessionRepository;

    public MessageService(
        IDirectSessionManager sessionManager,
        IMessageTransportService transportService,
        IConversationRepository conversationRepository,
        ILogger<MessageService> logger,
        ActiveIdentityContext activeIdentityContext,
        IDoubleRatchetSessionStore sessionStore,
        IDirectSessionRepository directSessionRepository)
    {
        _sessionManager = sessionManager;
        _transportService = transportService;
        _conversationRepository = conversationRepository;
        _logger = logger;
        _activeIdentityContext = activeIdentityContext;
        _sessionStore = sessionStore;
        _directSessionRepository = directSessionRepository;
    }

    public async Task SendDirectMessageAsync(
        DirectSessionId directSessionId, 
        string content,
        IdentityPeerId remotePeerId)
    {
        if (_activeIdentityContext.Identity is null)
        {
            throw new InvalidOperationException("Identity context not loaded");
        }
        var selfIdentityId = _activeIdentityContext.Identity.SelfIdentityId;
        var sessionId = new SessionId(directSessionId.Value);
        var sessionState = await _sessionStore.GetSessionStateAsync(sessionId, selfIdentityId);
        if (sessionState == null)
        {
            throw new InvalidOperationException($"Double Ratchet session state for conversation {directSessionId} not found.");
        }

        if (sessionState.TheirIdentityPublicKey == null)
        {
            throw new InvalidOperationException($"Double Ratchet session state for conversation {directSessionId} does not have their identity public key. There is no channel id for this conversation.");
        }
        _logger.LogInformation("Sending message to conversation {ConversationId}", directSessionId);
       
        // Create a proper InternalEnvelope with a ChatEnvelope containing a TextMessage
        var textMessage = new TextMessage
        {
            MessageId = ByteString.CopyFrom(Guid.NewGuid().ToByteArray()),
            SentTimestampUtc = Timestamp.FromDateTime(DateTime.UtcNow),
            Content = content
        };
        
        var chatEnvelope = new ChatEnvelope
        {
            TextMessage = textMessage
        };
        
        var internalEnvelope = new InternalEnvelope
        {
            ChatEnvelope = chatEnvelope
        };
        
        // Convert the protobuf message to plaintext bytes
        var plaintext = new Plaintext(internalEnvelope.ToByteArray());

        // Encrypt message using Double Ratchet
        var encryptedMessage = await _sessionManager.EncryptMessageAsync(sessionId, plaintext);
        
        // Send encrypted message using gRPC
        await _transportService.SendMessageAsync(
            remotePeerId,
            directSessionId,
            encryptedMessage);
        
        // Record message in local conversation
        var selfParticipantId = new ChatParticipantId(_activeIdentityContext.Identity.Id);
        var remoteParticipantId = new ChatParticipantId(remotePeerId.Value);
        
        // Get or create conversation
        var conversationId = new ChatConversationId(directSessionId.Value);
        var conversation = await _conversationRepository.GetByIdAsync(conversationId, selfIdentityId);
        if (conversation == null)
        {
            // Create a new conversation if it doesn't exist
            _logger.LogInformation("Creating new conversation {ConversationId}", conversationId);
            conversation = new Conversation(
                conversationId,
                new ChannelId(sessionState.TheirIdentityPublicKey.Value), // Generate a new channel ID for this conversation
                new List<ChatParticipantId> { selfParticipantId, remoteParticipantId },
                new List<Message>(),
                null // No name for direct conversations
            );
            
            // Add the message to the conversation
            _logger.LogInformation("Adding message to NEW conversation {ConversationId} with channel ID {ChannelId}", conversationId, Convert.ToBase64String(sessionState.TheirIdentityPublicKey.Value));
            conversation.AddMessage(selfParticipantId, content);
            await _conversationRepository.AddAsync(conversation, selfIdentityId);
        }
        else
        {
            conversation.AddMessage(selfParticipantId, content);
            _logger.LogInformation("Adding message to conversation {ConversationId} with channel ID {ChannelId}", conversationId, Convert.ToBase64String(sessionState.TheirIdentityPublicKey.Value));
            await _conversationRepository.UpdateAsync(conversation, selfIdentityId);
        }
        await _directSessionRepository.UpsertAsync(new Percolator.Network.PeerId(remoteParticipantId.Value), new DirectSessionId(conversation.Id.Value), selfIdentityId);
    }
}
