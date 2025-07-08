using System.Security.Cryptography;
using Percolator.Application.Identity;
using Percolator.Cryptography;
using Percolator.Sessions;
using SessionPeerId = Percolator.Sessions.PeerId;
using OpaquePublicKey = Percolator.Sessions.OpaquePublicKey;
using System.Collections.Concurrent;
using Percolator.Chat;
using SessionConversationId = Percolator.Sessions.ConversationId;

namespace Percolator.Application.Sessions;

public class DirectSessionManager
{
    private readonly IDoubleRatchetSessionStore _sessionStore;
    private readonly IConversationRepository _conversationRepository;
    private readonly ILocalPeerProvider _localPeerProvider;
    private readonly IMessageStore _messageStore;
    private readonly ActiveIdentityContext _activeIdentityContext;
    private readonly ConcurrentDictionary<SessionConversationId, SemaphoreSlim> _sessionLocks = new();

    public DirectSessionManager(
        IDoubleRatchetSessionStore sessionStore,
        IConversationRepository conversationRepository,
        ILocalPeerProvider localPeerProvider,
        IMessageStore messageStore,
        ActiveIdentityContext activeIdentityContext)
    {
        _sessionStore = sessionStore;
        _conversationRepository = conversationRepository;
        _localPeerProvider = localPeerProvider;
        _messageStore = messageStore;
        _activeIdentityContext = activeIdentityContext;
    }

    public async Task EstablishSessionAsInitiatorAsync(SessionConversationId conversationId, SessionPeerId remotePeerId, OpaquePublicKey remoteIdentityKey, OpaquePublicKey remoteRatchetKey, SharedSecret sharedSecret)
    {
        if (_activeIdentityContext.Keys is null)
            throw new InvalidOperationException("Identity context not loaded");

        using var session = DoubleRatchetSession.AsInitiator(
            sharedSecret,
            _activeIdentityContext.Keys.IdentityAgreementKey,
            new PublicKey(remoteIdentityKey.Value),
            new PublicKey(remoteRatchetKey.Value)
            );

        await _sessionStore.SaveSessionStateAsync(remotePeerId, conversationId, session.GetState());
        _sessionLocks.TryAdd(conversationId, new SemaphoreSlim(1, 1));
    }

    public async Task EstablishSessionAsResponderAsync(SessionConversationId conversationId, SessionPeerId remotePeerId, OpaquePublicKey remoteIdentityKey, SharedSecret sharedSecret)
    {
        if (_activeIdentityContext.Keys is null)
            throw new InvalidOperationException("Identity context not loaded");

        using var session = DoubleRatchetSession.AsResponder(
            sharedSecret,
            _activeIdentityContext.Keys.IdentityAgreementKey,
            new PublicKey(remoteIdentityKey.Value),
            _activeIdentityContext.Keys.SignedPreKey
            );

        await _sessionStore.SaveSessionStateAsync(remotePeerId, conversationId, session.GetState());
        _sessionLocks.TryAdd(conversationId, new SemaphoreSlim(1, 1));
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
            var remotePeerId = await GetRemotePeerId(conversationId);

            var sessionState = await _sessionStore.GetSessionStateAsync(remotePeerId, conversationId);
            if (sessionState == null)
            {
                throw new InvalidOperationException($"Double Ratchet session state for conversation {conversationId} not found.");
            }

            // DoubleRatchetSession will dispose the key, so we must pass a temporary copy.
            var identityKey = ECDiffieHellman.Create(_activeIdentityContext.Keys.IdentityAgreementKey.ExportParameters(true));

            using var doubleRatchetSession = new DoubleRatchetSession(sessionState, identityKey);

            var decryptedPlaintext = doubleRatchetSession.Decrypt(encryptedMessage);

            await _sessionStore.SaveSessionStateAsync(remotePeerId, conversationId, doubleRatchetSession.GetState());

            var message = new DirectMessage(new MessageId(Guid.NewGuid()), conversationId, remotePeerId, new OpaqueContent(decryptedPlaintext.Value));
            await _messageStore.StoreDirectMessageAsync(message);

            return decryptedPlaintext.Value;
        }
        finally
        {
            semaphore.Release();
        }
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
            var remotePeerId = await GetRemotePeerId(conversationId);
            var sessionState = await _sessionStore.GetSessionStateAsync(remotePeerId, conversationId);
            if (sessionState is null)
            {
                // This is the likely source of the error if the conversation exists but the session file doesn't.
                return null;
            }

            var identityKey = ECDiffieHellman.Create(_activeIdentityContext.Keys.IdentityAgreementKey.ExportParameters(true));
            using var doubleRatchetSession = new DoubleRatchetSession(sessionState, identityKey);

            var encryptedMessage = doubleRatchetSession.Encrypt(new Plaintext(plaintext));

            await _sessionStore.SaveSessionStateAsync(remotePeerId, conversationId, doubleRatchetSession.GetState());

            return (remotePeerId, encryptedMessage);
        }
        finally
        {
            semaphore.Release();
        }
    }

    private async Task<SessionPeerId> GetRemotePeerId(SessionConversationId conversationId)
    {
        var chatConversationId = new Percolator.Chat.ValueObjects.ConversationId(conversationId.Value);
        var conversation = await _conversationRepository.GetByIdAsync(chatConversationId);
        if (conversation is null)
        {
            throw new InvalidOperationException($"Conversation {conversationId} not found.");
        }

        var localPeerId = await _localPeerProvider.GetPeerIdAsync();

        var localParticipantId = new Chat.ValueObjects.ParticipantId(localPeerId.Value);
        var remoteParticipant = conversation.Participants.FirstOrDefault(p => p.Value != localParticipantId.Value);

        if (remoteParticipant.Value == Guid.Empty)
        {
            throw new InvalidOperationException("Could not determine remote peer in conversation.");
        }

        return new SessionPeerId(remoteParticipant.Value);
    }
}