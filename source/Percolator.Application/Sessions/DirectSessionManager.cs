using System.Security.Cryptography;
using System.Text;
using Percolator.Application.Identity;
using Percolator.Cryptography;
using Percolator.Sessions;
using SessionPeerId = Percolator.Sessions.PeerId;
using OpaquePublicKey = Percolator.Sessions.OpaquePublicKey;
using System.Collections.Concurrent;

namespace Percolator.Application.Sessions;

public class DirectSessionManager
{
    private readonly IDoubleRatchetSessionStore _doubleRatchetSessionStore;
    private readonly IConversationStore _conversationStore;
    private readonly IMessageStore _messageStore;
    private readonly ActiveIdentityContext _activeIdentityContext;
    private readonly ConcurrentDictionary<ConversationId, SemaphoreSlim> _sessionLocks = new();

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

    public async Task<ConversationId> EstablishSessionAsync(SessionPeerId remotePeerId, OpaquePublicKey remoteIdentityPublicKey, SharedSecret sharedSecret)
    {
        if (_activeIdentityContext.Identity is null || _activeIdentityContext.Keys is null)
        {
            throw new InvalidOperationException("No active identity found to establish session.");
        }

        // DoubleRatchetSession will dispose the key, so we must pass a temporary copy.
        var identityKey = ECDiffieHellman.Create(_activeIdentityContext.Keys.IdentityAgreementKey.ExportParameters(true));
        var localRatchetKey = ECDiffieHellman.Create(_activeIdentityContext.Keys.SignedPreKey.ExportParameters(true));

        using var doubleRatchetSession = DoubleRatchetSession.AsResponder(
            sharedSecret.Value,
            identityKey,
            remoteIdentityPublicKey.Value,
            localRatchetKey
        );

        var conversationId = new ConversationId(Guid.NewGuid());
        var localPeerId = new SessionPeerId(_activeIdentityContext.Identity.Id);
        var conversation = new DirectConversation(conversationId, localPeerId, remotePeerId);

        await _doubleRatchetSessionStore.SaveSessionStateAsync(remotePeerId, conversationId, doubleRatchetSession.GetState());
        await _conversationStore.SaveConversationAsync(conversation);

        // Initialize a lock for the new session to prevent race conditions during message processing
        _sessionLocks.TryAdd(conversationId, new SemaphoreSlim(1, 1));

        return conversationId;
    }

    public async Task<byte[]> ReceiveMessageAsync(ConversationId conversationId, RatchetMessage encryptedMessage)
    {
        if (_activeIdentityContext.Identity is null || _activeIdentityContext.Keys is null)
        {
            throw new InvalidOperationException("No active identity found to receive message.");
        }

        // Ensure only one message is processed at a time for a given conversation to prevent race conditions.
        var semaphore = _sessionLocks.GetOrAdd(conversationId, new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync();

        try
        {
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
        finally
        {
            semaphore.Release();
        }
    }

    public async Task<DirectConversation?> GetConversationAsync(ConversationId conversationId)
    {
        return await _conversationStore.GetConversationAsync(conversationId);
    }

    public async Task<(SessionPeerId RemotePeerId, RatchetMessage EncryptedMessage)?> EncryptMessageAsync(ConversationId conversationId, byte[] plaintext)
    {
        if (_activeIdentityContext.Identity is null || _activeIdentityContext.Keys is null)
        {
            throw new InvalidOperationException("No active identity found to encrypt message.");
        }

        var semaphore = _sessionLocks.GetOrAdd(conversationId, new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync();

        try
        {
            var conversation = await _conversationStore.GetConversationAsync(conversationId);
            if (conversation is null)
            {
                return null;
            }

            var remotePeerId = conversation.RemotePeerId;
            var sessionState = await _doubleRatchetSessionStore.GetSessionStateAsync(remotePeerId, conversationId);
            if (sessionState is null)
            {
                throw new InvalidOperationException($"Double Ratchet session state for conversation {conversationId} not found.");
            }

            var identityKey = ECDiffieHellman.Create(_activeIdentityContext.Keys.IdentityAgreementKey.ExportParameters(true));
            using var doubleRatchetSession = new DoubleRatchetSession(sessionState, identityKey);

            var encryptedMessage = doubleRatchetSession.Encrypt(plaintext);

            await _doubleRatchetSessionStore.SaveSessionStateAsync(remotePeerId, conversationId, doubleRatchetSession.GetState());

            return (remotePeerId, encryptedMessage);
        }
        finally
        {
            semaphore.Release();
        }
    }
}