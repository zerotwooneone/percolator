using Percolator.Application.Identity;
using SessionPeerId = Percolator.Sessions.PeerId;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Percolator.Chat;
using Percolator.Cryptography;
using SessionConversationId = Percolator.Sessions.ConversationId;

namespace Percolator.Application.Sessions;

/// <summary>
/// Manages Double Ratchet sessions for direct messaging, working directly with cryptography primitives.
/// </summary>
public class DirectSessionManager : IDirectSessionManager
{
    private readonly IDoubleRatchetSessionStore _sessionStore;
    private readonly IConversationRepository _conversationRepository;
    private readonly ActiveIdentityContext _activeIdentityContext;
    private readonly ILogger<DirectSessionManager> _logger;
    private readonly ConcurrentDictionary<SessionConversationId, SemaphoreSlim> _sessionLocks = new();

    public DirectSessionManager(
        IDoubleRatchetSessionStore sessionStore,
        IConversationRepository conversationRepository,
        ActiveIdentityContext activeIdentityContext,
        ILogger<DirectSessionManager> logger)
    {
        _sessionStore = sessionStore;
        _conversationRepository = conversationRepository;
        _activeIdentityContext = activeIdentityContext;
        _logger = logger;
    }

    public async Task EstablishSessionAsInitiatorAsync(
        SessionConversationId conversationId, 
        SessionPeerId remotePeerId, 
        RatchetIdentityKey remoteIdentityKey, 
        RatchetEphemeralKey remoteRatchetKey, 
        SharedSecret sharedSecret)
    {
        if (_activeIdentityContext.Keys is null)
            throw new InvalidOperationException("Identity context not loaded");

        // Create the session directly in Crypto domain
        var session = DoubleRatchetSession.AsInitiator(
            sharedSecret,
            remoteIdentityKey,
            remoteRatchetKey);

        var sessionId = new SessionId(conversationId.Value);
        _logger.LogInformation("Establish session as initiator for conversation {ConversationId}. SessionId: {SessionId}", conversationId, sessionId);
        
        // Get state and store it
        await _sessionStore.SetSessionStateAsync(sessionId, session.GetState());
        _sessionLocks.TryAdd(conversationId, new SemaphoreSlim(1, 1));
    }

    public async Task EstablishSessionAsResponderAsync(
        SessionConversationId conversationId, 
        SessionPeerId remotePeerId, 
        RatchetIdentityKey remoteIdentityKey,
        SharedSecret sharedSecret)
    {
        if (_activeIdentityContext.Keys is null)
            throw new InvalidOperationException("Identity context not loaded");

        // Create the ECDiffieHellman key
        using var localRatchetKey = ECDiffieHellman.Create();
        localRatchetKey.ImportECPrivateKey(_activeIdentityContext.Keys.SignedPreKey.ExportECPrivateKey(), out _);

        // Create the session directly in Crypto domain
        var session = DoubleRatchetSession.AsResponder(
            sharedSecret,
            remoteIdentityKey,
            localRatchetKey);

        var sessionId = new SessionId(conversationId.Value);
        _logger.LogInformation("Establish session as responder for conversation {ConversationId}. SessionId: {SessionId}", conversationId, sessionId);
        
        // Get state and store it
        await _sessionStore.SetSessionStateAsync(sessionId, session.GetState());
        _sessionLocks.TryAdd(conversationId, new SemaphoreSlim(1, 1));
    }

    public async Task<Plaintext?> ReceiveMessageAsync(
        SessionConversationId conversationId, 
        SessionRatchetMessage encryptedMessage)
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
            var conversation = await _conversationRepository.GetByIdAsync(new Chat.ValueObjects.ConversationId(conversationId.Value));
            if (conversation is null)
                throw new InvalidOperationException($"Conversation with id {conversationId} not found");
            var remotePeerId = await GetRemotePeerIdFromDirectMessage(conversation);

            var sessionId = new SessionId(conversationId.Value);
            _logger.LogInformation("Receive message for conversation {ConversationId}. SessionId: {SessionId}", conversationId, sessionId);

            var sessionState = await _sessionStore.GetSessionStateAsync(sessionId);
            if (sessionState == null)
            {
                throw new InvalidOperationException($"Double Ratchet session state for conversation {conversationId} not found.");
            }
            
            // Use Crypto domain directly
            using var session = new DoubleRatchetSession(sessionState);
            var decryptedPlaintext = session.Decrypt(encryptedMessage);

            // Save the updated state
            await _sessionStore.SetSessionStateAsync(sessionId, session.GetState());

            if (decryptedPlaintext is null)
            {
                // This can happen if the message was a skipped message that was already processed.
                // In this case, we don't need to do anything.
                return null;
            }

            return decryptedPlaintext;
        }
        finally
        {
            semaphore.Release();
        }
    }

    public async Task<(SessionPeerId remotePeerId, SessionRatchetMessage encryptedMessage)?> EncryptMessageAsync(
        SessionConversationId conversationId, 
        Plaintext plaintext)
    {
        // Ensure only one message is processed at a time for a given conversation to prevent race conditions.
        var semaphore = _sessionLocks.GetOrAdd(conversationId, new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync();

        try
        {
            var conversation = await _conversationRepository.GetByIdAsync(new Chat.ValueObjects.ConversationId(conversationId.Value));
            if (conversation is null)
                throw new InvalidOperationException($"Conversation with id {conversationId} not found");
            var remotePeerId = await GetRemotePeerIdFromDirectMessage(conversation);

            var sessionId = new SessionId(conversationId.Value);
            _logger.LogInformation("Encrypt message for conversation {ConversationId}. SessionId: {SessionId}", conversationId, sessionId);
            
            var sessionState = await _sessionStore.GetSessionStateAsync(sessionId);
            if (sessionState == null)
            {
                return null;
            }
            
            // Use Crypto domain directly
            using var session = new DoubleRatchetSession(sessionState);
            var encryptedMessage = session.Encrypt(plaintext);

            // Save the updated state
            await _sessionStore.SetSessionStateAsync(sessionId, session.GetState());

            return (remotePeerId, encryptedMessage);
        }
        finally
        {
            semaphore.Release();
        }
    }

    private async Task<SessionPeerId> GetRemotePeerIdFromDirectMessage(Conversation conversation)
    {
        if (_activeIdentityContext.Identity is null)
            throw new InvalidOperationException("Identity context not loaded");

        var localPeerId = _activeIdentityContext.Identity.Id;
        var remotePeerId = conversation.Participants.First(p => p.Value != localPeerId);
        return new SessionPeerId(remotePeerId.Value);
    }
}
