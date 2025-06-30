using System;
using System.Text;
using System.Threading.Tasks;
using Percolator.Application.Identity;
using Percolator.Cryptography;
using Percolator.Identity;
using Percolator.Sessions;
using SessionPeerId = Percolator.Sessions.PeerId;
using OpaquePublicKey = Percolator.Sessions.OpaquePublicKey;

namespace Percolator.Application.Sessions;

public class DirectSessionManager
{
    private readonly IDoubleRatchetSessionStore _doubleRatchetSessionStore;
    private readonly IConversationStore _conversationStore;
    private readonly IMessageStore _messageStore;
    private readonly ActiveIdentityContext _activeIdentityContext;

    public DirectSessionManager(
        IDoubleRatchetSessionStore doubleRatchetSessionStore,
        IConversationStore conversationStore,
        IMessageStore messageStore,
        ActiveIdentityContext activeIdentityContext)
    {
        _doubleRatchetSessionStore = doubleRatchetSessionStore;
        _conversationStore = conversationStore;
        _messageStore = messageStore;
        _activeIdentityContext = activeIdentityContext;
    }

    public async Task<ConversationId> EstablishSessionAsync(SessionPeerId remotePeerId, SharedSecret sharedSecret, OpaquePublicKey initialRatchetPublicKey)
    {
        var activeIdentity = _activeIdentityContext;
        if (activeIdentity?.Certificate is null || activeIdentity.IdentityName is null)
        {
            throw new InvalidOperationException("No active identity found to establish session.");
        }

        using var identityKey = activeIdentity.Certificate.GetECDHKeyPair();

        using var doubleRatchetSession = DoubleRatchetSession.AsInitiator(
            sharedSecret.Value,
            identityKey,
            remotePeerId.Value.ToByteArray(), // This needs to be the remote peer's public identity key
            initialRatchetPublicKey.Value
        );

        var conversationId = new ConversationId(Guid.NewGuid());
        var localPeerId = new SessionPeerId(Guid.Parse(activeIdentity.IdentityName));
        var conversation = new DirectConversation(conversationId, localPeerId, remotePeerId);

        await _doubleRatchetSessionStore.SaveSessionStateAsync(remotePeerId, conversationId, doubleRatchetSession.GetState());
        await _conversationStore.SaveConversationAsync(conversation);

        return conversationId;
    }

    public async Task<byte[]> ReceiveMessageAsync(ConversationId conversationId, RatchetMessage encryptedMessage)
    {
        var activeIdentity = _activeIdentityContext;
        if (activeIdentity?.Certificate is null || activeIdentity.IdentityName is null)
        {
            throw new InvalidOperationException("No active identity found to receive message.");
        }
        
        var conversation = await _conversationStore.GetConversationAsync(conversationId);
        if (conversation == null)
        {
            throw new InvalidOperationException($"Conversation with ID {conversationId} not found.");
        }

        var remotePeerId = conversation.RemotePeerId;

        var sessionState = await _doubleRatchetSessionStore.GetSessionStateAsync(remotePeerId, conversationId);
        if (sessionState == null)
        {
            throw new InvalidOperationException($"Double Ratchet session state for conversation {conversationId} not found.");
        }
        
        using var identityKey = activeIdentity.Certificate.GetECDHKeyPair();

        using var doubleRatchetSession = new DoubleRatchetSession(sessionState, identityKey);

        var decryptedBytes = doubleRatchetSession.Decrypt(encryptedMessage);

        await _doubleRatchetSessionStore.SaveSessionStateAsync(remotePeerId, conversationId, doubleRatchetSession.GetState());

        var message = new DirectMessage(new MessageId(Guid.NewGuid()), conversationId, remotePeerId, new OpaqueContent(encryptedMessage.Ciphertext));
        await _messageStore.StoreDirectMessageAsync(message);

        return decryptedBytes;
    }

    public async Task<DirectConversation?> GetConversationAsync(ConversationId conversationId)
    {
        return await _conversationStore.GetConversationAsync(conversationId);
    }
}