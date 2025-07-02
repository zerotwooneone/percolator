using System;
using System.Security.Cryptography;
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

    public async Task<ConversationId> EstablishSessionAsync(SessionPeerId remotePeerId, OpaquePublicKey remoteIdentityPublicKey, SharedSecret sharedSecret, OpaquePublicKey initialRatchetPublicKey)
    {
        if (_activeIdentityContext.Identity is null || _activeIdentityContext.Keys is null)
        {
            throw new InvalidOperationException("No active identity found to establish session.");
        }

        // DoubleRatchetSession will dispose the key, so we must pass a temporary copy.
        var identityKey = ECDiffieHellman.Create(_activeIdentityContext.Keys.IdentityAgreementKey.ExportParameters(true));

        using var doubleRatchetSession = DoubleRatchetSession.AsInitiator(
            sharedSecret.Value,
            identityKey,
            remoteIdentityPublicKey.Value, 
            initialRatchetPublicKey.Value
        );

        var conversationId = new ConversationId(Guid.NewGuid());
        var localPeerId = new SessionPeerId(_activeIdentityContext.Identity.Id);
        var conversation = new DirectConversation(conversationId, localPeerId, remotePeerId);

        await _doubleRatchetSessionStore.SaveSessionStateAsync(remotePeerId, conversationId, doubleRatchetSession.GetState());
        await _conversationStore.SaveConversationAsync(conversation);

        return conversationId;
    }

    public async Task<byte[]> ReceiveMessageAsync(ConversationId conversationId, RatchetMessage encryptedMessage)
    {
        if (_activeIdentityContext.Identity is null || _activeIdentityContext.Keys is null)
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

        // DoubleRatchetSession will dispose the key, so we must pass a temporary copy.
        var identityKey = ECDiffieHellman.Create(_activeIdentityContext.Keys.IdentityAgreementKey.ExportParameters(true));

        using var doubleRatchetSession = new DoubleRatchetSession(sessionState, identityKey);

        var decryptedBytes = doubleRatchetSession.Decrypt(encryptedMessage);

        await _doubleRatchetSessionStore.SaveSessionStateAsync(remotePeerId, conversationId, doubleRatchetSession.GetState());

        var message = new DirectMessage(new MessageId(Guid.NewGuid()), conversationId, remotePeerId, new OpaqueContent(decryptedBytes));
        await _messageStore.StoreDirectMessageAsync(message);

        return decryptedBytes;
    }

    public async Task<RatchetMessage> SendMessageAsync(ConversationId conversationId, string plaintext)
    {
        if (_activeIdentityContext.Identity is null || _activeIdentityContext.Keys is null)
        {
            throw new InvalidOperationException("No active identity found to send message.");
        }

        var conversation = await _conversationStore.GetConversationAsync(conversationId);
        if (conversation is null)
        {
            throw new InvalidOperationException($"Conversation with ID {conversationId} not found.");
        }

        var remotePeerId = conversation.RemotePeerId;

        var sessionState = await _doubleRatchetSessionStore.GetSessionStateAsync(remotePeerId, conversationId);
        if (sessionState is null)
        {
            throw new InvalidOperationException($"Double Ratchet session state for conversation {conversationId} not found.");
        }

        var identityKey = ECDiffieHellman.Create(_activeIdentityContext.Keys.IdentityAgreementKey.ExportParameters(true));

        using var doubleRatchetSession = new DoubleRatchetSession(sessionState, identityKey);

        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
        var encryptedMessage = doubleRatchetSession.Encrypt(plaintextBytes);

        await _doubleRatchetSessionStore.SaveSessionStateAsync(remotePeerId, conversationId, doubleRatchetSession.GetState());

        var localPeerId = new SessionPeerId(_activeIdentityContext.Identity.Id);
        var message = new DirectMessage(
            new MessageId(Guid.NewGuid()),
            conversationId,
            localPeerId,
            new OpaqueContent(plaintextBytes)
        );
        await _messageStore.StoreDirectMessageAsync(message);

        return encryptedMessage;
    }

    public async Task<DirectConversation?> GetConversationAsync(ConversationId conversationId)
    {
        return await _conversationStore.GetConversationAsync(conversationId);
    }
}