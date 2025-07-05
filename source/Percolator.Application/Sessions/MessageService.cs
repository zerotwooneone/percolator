using System.Text;
using Percolator.Application.Network;
using Percolator.Sessions;
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
    private readonly ILocalPeerProvider _localPeerProvider;
    private readonly IMessageStore _messageStore;
    private readonly DirectSessionManager _sessionManager;
    private readonly IMessageTransportService _transportService;

    public MessageService(
        ILocalPeerProvider localPeerProvider, 
        IMessageStore messageStore, 
        DirectSessionManager sessionManager, 
        IMessageTransportService transportService)
    {
        _localPeerProvider = localPeerProvider;
        _messageStore = messageStore;
        _sessionManager = sessionManager;
        _transportService = transportService;
    }

    public async Task<ChatMessage> SendDirectMessageAsync(
        ChatConversationId conversationId,
        string content)
    {
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
        var senderId = await _localPeerProvider.GetPeerIdAsync();
        var sessionMessageId = SessionMessageId.NewId();
        var opaqueContent = new OpaqueContent(contentBytes); // Store plaintext for local history

        var directMessage = new DirectMessage(
            sessionMessageId,
            sessionConversationId,
            senderId,
            opaqueContent
        );

        await _messageStore.StoreDirectMessageAsync(directMessage);

        var chatMessageId = new ChatMessageId(sessionMessageId.Value);
        var chatSenderId = new ChatParticipantId(senderId.Value);

        return new ChatMessage(chatMessageId, chatSenderId, content, directMessage.Timestamp);
    }
}
