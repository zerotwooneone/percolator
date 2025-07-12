using System.Text;
using Percolator.Application.Identity;
using Percolator.Application.Network;
using Percolator.Sessions;
using SessionPeerId = Percolator.Sessions.PeerId;
using ChatConversationId = Percolator.Chat.ValueObjects.ConversationId;
using ChatMessage = Percolator.Chat.Message;
using ChatMessageId = Percolator.Chat.ValueObjects.MessageId;
using ChatParticipantId = Percolator.Chat.ValueObjects.ParticipantId;
using IdentityPeerId = Percolator.Identity.PeerId;
using SessionConversationId = Percolator.Sessions.ConversationId;
using SessionMessageId = Percolator.Sessions.MessageId;

namespace Percolator.Application.Sessions;

public class MessageService : IMessageService
{
    private readonly IMessageStore _messageStore;
    private readonly IDirectSessionManager _sessionManager;
    private readonly IMessageTransportService _transportService;
    private readonly ActiveIdentityContext _activeIdentityContext;

    public MessageService(
        IMessageStore messageStore, 
        IDirectSessionManager sessionManager, 
        IMessageTransportService transportService,
        ActiveIdentityContext activeIdentityContext)
    {
        _messageStore = messageStore;
        _sessionManager = sessionManager;
        _transportService = transportService;
        _activeIdentityContext = activeIdentityContext;
    }

    public async Task<ChatMessage> SendDirectMessageAsync(
        ChatConversationId conversationId,
        string content)
    {
        if (_activeIdentityContext.Identity is null)
        {
            throw new InvalidOperationException("Identity context not loaded");
        }
        var contentBytes = Encoding.UTF8.GetBytes(content);
        var sessionConversationId = new SessionConversationId(conversationId.Value);

        var encryptionResult = await _sessionManager.EncryptMessageAsync(sessionConversationId, contentBytes);
        if (encryptionResult is null)
        {
            // Or throw an exception, depending on desired error handling
            throw new InvalidOperationException($"Failed to encrypt message. Conversation {conversationId} not found.");
        }

        var (remotePeerId, encryptedMessage) = encryptionResult.Value;
        var identityPeerId = new IdentityPeerId(remotePeerId.Value);

        // Asynchronously send the message over the network
        await _transportService.SendMessageAsync(identityPeerId, conversationId, encryptedMessage);

        // For local storage and immediate feedback, create and store the message object.
        var sessionMessageId = SessionMessageId.NewId();
        var opaqueContent = new OpaqueContent(contentBytes); // Store plaintext for local history

        var directMessage = new DirectMessage(
            sessionMessageId,
            sessionConversationId,
            new SessionPeerId(_activeIdentityContext.Identity.Id),
            opaqueContent
        );

        await _messageStore.StoreDirectMessageAsync(directMessage);

        var chatMessageId = new ChatMessageId(sessionMessageId.Value);
        var chatSenderId = new ChatParticipantId(_activeIdentityContext.Identity.Id);

        return new ChatMessage(chatMessageId, chatSenderId, content, directMessage.Timestamp);
    }
}
