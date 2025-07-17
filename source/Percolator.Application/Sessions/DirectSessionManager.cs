using Percolator.Application.Identity;
using Percolator.Sessions;
using SessionPeerId = Percolator.Sessions.PeerId;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Percolator.Chat;
using SessionConversationId = Percolator.Sessions.ConversationId;

namespace Percolator.Application.Sessions;

public class DirectSessionManager : IDirectSessionManager
{
    private readonly IDoubleRatchetSessionStore _sessionStore;
    private readonly IConversationRepository _conversationRepository;
    private readonly IMessageStore _messageStore;
    private readonly ActiveIdentityContext _activeIdentityContext;
    private readonly IDoubleRatchetProtocol _doubleRatchetProtocol;
    private readonly ILogger<DirectSessionManager> _logger;
    private readonly ConcurrentDictionary<SessionConversationId, SemaphoreSlim> _sessionLocks = new();

    public DirectSessionManager(
        IDoubleRatchetSessionStore sessionStore,
        IConversationRepository conversationRepository,
        IMessageStore messageStore,
        ActiveIdentityContext activeIdentityContext,
        IDoubleRatchetProtocol doubleRatchetProtocol,
        ILogger<DirectSessionManager> logger)
    {
        _sessionStore = sessionStore;
        _conversationRepository = conversationRepository;
        _messageStore = messageStore;
        _activeIdentityContext = activeIdentityContext;
        _doubleRatchetProtocol = doubleRatchetProtocol;
        _logger = logger;
    }

    public async Task EstablishSessionAsInitiatorAsync(SessionConversationId conversationId, SessionPeerId remotePeerId, SessionIdentityKey remoteIdentityKey, SessionRatchetKey remoteRatchetKey, SharedSecret sharedSecret)
    {
        if (_activeIdentityContext.Keys is null)
            throw new InvalidOperationException("Identity context not loaded");

        var sessionState = _doubleRatchetProtocol.InitiateSession(
            new RatchetIdentityKey(remoteIdentityKey.Value),
            new RatchetEphemeralKey(remoteRatchetKey.Value),
            sharedSecret);

        var sessionId = GetSessionId(remotePeerId, conversationId);
        _logger.LogInformation("Establish session as initiator for conversation {ConversationId}. SessionId: {SessionId}", conversationId, sessionId);
        await _sessionStore.SetSessionStateAsync(sessionId, sessionState);
        _sessionLocks.TryAdd(conversationId, new SemaphoreSlim(1, 1));
    }

    public async Task EstablishSessionAsResponderAsync(SessionConversationId conversationId, SessionPeerId remotePeerId, SessionIdentityKey remoteIdentityKey, SharedSecret sharedSecret)
    {
        if (_activeIdentityContext.Keys is null)
            throw new InvalidOperationException("Identity context not loaded");

        var sessionState = _doubleRatchetProtocol.RespondToSession(
            new RatchetIdentityKey(remoteIdentityKey.Value),
            new PrivateEphemeralKey(_activeIdentityContext.Keys.SignedPreKey.ExportECPrivateKey()),
            sharedSecret);

        var sessionId = GetSessionId(remotePeerId, conversationId);
        await _sessionStore.SetSessionStateAsync(sessionId, sessionState);
        _logger.LogInformation("Establish as responder for conversation {ConversationId}. SessionId: {SessionId}", conversationId, sessionId);
        _sessionLocks.TryAdd(conversationId, new SemaphoreSlim(1, 1));
    }

    public async Task<Plaintext?> ReceiveMessageAsync(SessionConversationId conversationId, RatchetMessage encryptedMessage)
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
            _logger.LogInformation("Receive message for conversation {ConversationId}. SessionId: {SessionId}", conversationId, sessionId);
            var sessionState = await _sessionStore.GetSessionStateAsync(sessionId);
            if (sessionState == null)
            {
                throw new InvalidOperationException($"Double Ratchet session state for conversation {conversationId} not found. SessionId: {sessionId}");
            }

            var (newState, decryptedPlaintext) = _doubleRatchetProtocol.Decrypt(sessionState, encryptedMessage);

            await _sessionStore.SetSessionStateAsync(sessionId, newState);

            if (decryptedPlaintext is null)
            {
                // This can happen if the message was a skipped message that was already processed.
                // In this case, we don't need to do anything.
                return null;
            }
            
            var message = new DirectMessage(new MessageId(Guid.NewGuid()), conversationId, remotePeerId, new OpaqueContent(decryptedPlaintext.Value));
            await _messageStore.StoreDirectMessageAsync(message);

            return decryptedPlaintext;
        }
        finally
        {
            semaphore.Release();
        }
    }

    public async Task<(SessionPeerId remotePeerId, RatchetMessage encryptedMessage)?> EncryptMessageAsync(SessionConversationId conversationId, Plaintext plaintext)
    {
        // Ensure only one message is processed at a time for a given conversation to prevent race conditions.
        var semaphore = _sessionLocks.GetOrAdd(conversationId, new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync();

        try
        {
            var remotePeerId = await GetRemotePeerId(conversationId);

            var sessionId = GetSessionId(remotePeerId, conversationId);
            _logger.LogInformation("Encrypt message for conversation {ConversationId}. SessionId: {SessionId}", conversationId, sessionId);
            var sessionState = await _sessionStore.GetSessionStateAsync(sessionId);
            if (sessionState == null)
            {
                return null;
            }

            var (newState, encryptedMessage) = _doubleRatchetProtocol.Encrypt(sessionState, plaintext);

            await _sessionStore.SetSessionStateAsync(sessionId, newState);

            return (remotePeerId, encryptedMessage);
        }
        finally
        {
            semaphore.Release();
        }
    }

    private async Task<SessionPeerId> GetRemotePeerId(SessionConversationId conversationId)
    {
        var conversation = await _conversationRepository.GetByIdAsync(new Chat.ValueObjects.ConversationId(conversationId.Value));
        if (conversation is null)
            throw new InvalidOperationException($"Conversation with id {conversationId} not found");

        if (_activeIdentityContext.Identity is null)
            throw new InvalidOperationException("Identity context not loaded");

        var localPeerId = _activeIdentityContext.Identity.Id;
        
        var remotePeerId = conversation.Participants.First(p => p.Value != localPeerId);
        _logger.LogInformation("Local peer ID: {LocalPeerId} Remote peer ID: {RemotePeerId}", localPeerId, remotePeerId);
        return new SessionPeerId(remotePeerId.Value);
    }

    private static string GetSessionId(SessionPeerId remotePeerId, SessionConversationId conversationId) =>
        $"{remotePeerId.Value}-{conversationId.Value}";
}