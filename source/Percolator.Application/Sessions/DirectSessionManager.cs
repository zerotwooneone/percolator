using System.Security.Cryptography;
using Percolator.Application.Identity;
using Percolator.Cryptography;
using Percolator.Sessions;
using SessionPeerId = Percolator.Sessions.PeerId;
using System.Collections.Concurrent;
using Percolator.Chat;
using SessionConversationId = Percolator.Sessions.ConversationId;

namespace Percolator.Application.Sessions;

public class DirectSessionManager : IDirectSessionManager
{
    private readonly IDoubleRatchetSessionStore _sessionStore;
    private readonly IConversationRepository _conversationRepository;
    private readonly IMessageStore _messageStore;
    private readonly ActiveIdentityContext _activeIdentityContext;
    private readonly ConcurrentDictionary<SessionConversationId, SemaphoreSlim> _sessionLocks = new();

    public DirectSessionManager(
        IDoubleRatchetSessionStore sessionStore,
        IConversationRepository conversationRepository,
        IMessageStore messageStore,
        ActiveIdentityContext activeIdentityContext)
    {
        _sessionStore = sessionStore;
        _conversationRepository = conversationRepository;
        _messageStore = messageStore;
        _activeIdentityContext = activeIdentityContext;
    }

    public async Task EstablishSessionAsInitiatorAsync(SessionConversationId conversationId, SessionPeerId remotePeerId, SessionIdentityKey remoteIdentityKey, SessionRatchetKey remoteRatchetKey, SharedSecret sharedSecret)
    {
        if (_activeIdentityContext.Keys is null)
            throw new InvalidOperationException("Identity context not loaded");

        using var session = DoubleRatchetSession.AsInitiator(
            sharedSecret,
            new RatchetIdentityKey(remoteIdentityKey.Value),
            new RatchetEphemeralKey(remoteRatchetKey.Value)
            );

        var sessionId = GetSessionId(remotePeerId, conversationId);
        await _sessionStore.SetSessionStateAsync(sessionId, session.GetState());
        _sessionLocks.TryAdd(conversationId, new SemaphoreSlim(1, 1));
    }

    public async Task EstablishSessionAsResponderAsync(SessionConversationId conversationId, SessionPeerId remotePeerId, SessionIdentityKey remoteIdentityKey, SharedSecret sharedSecret)
    {
        if (_activeIdentityContext.Keys is null)
            throw new InvalidOperationException("Identity context not loaded");

        using var session = DoubleRatchetSession.AsResponder(
            sharedSecret,
            new RatchetIdentityKey(remoteIdentityKey.Value),
            _activeIdentityContext.Keys.SignedPreKey
            );

        var sessionId = GetSessionId(remotePeerId, conversationId);
        await _sessionStore.SetSessionStateAsync(sessionId, session.GetState());
        _sessionLocks.TryAdd(conversationId, new SemaphoreSlim(1, 1));
    }

    public async Task<byte[]> ReceiveMessageAsync(SessionConversationId conversationId, RatchetMessage encryptedMessage)
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

            var sessionId = GetSessionId(remotePeerId, conversationId);
            var sessionState = await _sessionStore.GetSessionStateAsync(sessionId);
            if (sessionState == null)
            {
                throw new InvalidOperationException($"Double Ratchet session state for conversation {conversationId} not found.");
            }

            // DoubleRatchetSession will dispose the key, so we must pass a temporary copy.
            var identityKey = ECDiffieHellman.Create(_activeIdentityContext.Keys.IdentityAgreementKey.ExportParameters(true));

            using var doubleRatchetSession = new DoubleRatchetSession(sessionState);

            var decryptedPlaintext = doubleRatchetSession.Decrypt(encryptedMessage);

            await _sessionStore.SetSessionStateAsync(sessionId, doubleRatchetSession.GetState());

            var message = new DirectMessage(new MessageId(Guid.NewGuid()), conversationId, remotePeerId, new OpaqueContent(decryptedPlaintext.Value));
            await _messageStore.StoreDirectMessageAsync(message);

            return decryptedPlaintext.Value;
        }
        finally
        {
            semaphore.Release();
        }
    }

    public async Task<(SessionPeerId RemotePeerId, RatchetMessage EncryptedMessage)?> EncryptMessageAsync(SessionConversationId conversationId, byte[] plaintext)
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
            var sessionId = GetSessionId(remotePeerId, conversationId);
            var sessionState = await _sessionStore.GetSessionStateAsync(sessionId);
            if (sessionState is null)
            {
                // This is the likely source of the error if the conversation exists but the session file doesn't.
                return null;
            }

            var identityKey = ECDiffieHellman.Create(_activeIdentityContext.Keys.IdentityAgreementKey.ExportParameters(true));
            using var doubleRatchetSession = new DoubleRatchetSession(sessionState);

            var encryptedMessage = doubleRatchetSession.Encrypt(new Plaintext(plaintext));

            await _sessionStore.SetSessionStateAsync(sessionId, doubleRatchetSession.GetState());

            return (remotePeerId, encryptedMessage);
        }
        finally
        {
            semaphore.Release();
        }
    }

    private async Task<SessionPeerId> GetRemotePeerId(SessionConversationId conversationId)
    {
        if (_activeIdentityContext.Identity is null)
        {
            throw new InvalidOperationException("No active identity found to get remote peer ID.");
        }
        var chatConversationId = new Percolator.Chat.ValueObjects.ConversationId(conversationId.Value);
        var conversation = await _conversationRepository.GetByIdAsync(chatConversationId);
        if (conversation is null)
        {
            throw new InvalidOperationException($"Conversation {conversationId} not found.");
        }

        var localParticipantId = new Chat.ValueObjects.ParticipantId(_activeIdentityContext.Identity.Id);
        var remoteParticipant = conversation.Participants.FirstOrDefault(p => p.Value != localParticipantId.Value);

        if (remoteParticipant.Value == Guid.Empty)
        {
            throw new InvalidOperationException("Could not determine remote peer in conversation.");
        }

        return new SessionPeerId(remoteParticipant.Value);
    }

    private static string GetSessionId(SessionPeerId peerId, SessionConversationId conversationId) =>
        $"{peerId.Value}-{conversationId.Value}";
}