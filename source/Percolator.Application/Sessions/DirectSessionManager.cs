using Percolator.Application.Identity;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Percolator.Chat;
using Percolator.Cryptography;
using Percolator.Identity;

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
    private readonly ILoggerFactory _loggerFactory;
    private readonly IOptions<CryptographyOptions> _cryptographyOptions;
    private readonly ConcurrentDictionary<SessionId, SemaphoreSlim> _sessionLocks = new();

    public DirectSessionManager(
        IDoubleRatchetSessionStore sessionStore,
        IConversationRepository conversationRepository,
        ActiveIdentityContext activeIdentityContext,
        ILogger<DirectSessionManager> logger,
        ILoggerFactory loggerFactory,
        IOptions<CryptographyOptions> cryptographyOptions)
    {
        _sessionStore = sessionStore;
        _conversationRepository = conversationRepository;
        _activeIdentityContext = activeIdentityContext;
        _logger = logger;
        _loggerFactory = loggerFactory;
        _cryptographyOptions = cryptographyOptions;
    }

    public async Task EstablishSessionAsInitiatorAsync(SessionId conversationId,
        PeerId remotePeerId,
        RatchetIdentityKey remoteIdentityKey,
        RatchetEphemeralKey remoteRatchetKey,
        SharedSecret sharedSecret, 
        ECDiffieHellman localEphemeralKey)
    {
        if (_activeIdentityContext.Keys is null)
            throw new InvalidOperationException("Identity context not loaded");

        if (_cryptographyOptions.Value.EnableCryptographicMaterialLogging)
        {
            _logger.LogInformation("Initiator establishing session with remote ratchet key : {RemoteRatchetKey}, shared secret : {SharedSecret}", 
                Convert.ToBase64String(remoteRatchetKey.Value),
                Convert.ToBase64String(sharedSecret.Value));
        }
        
        // Create the session directly in Crypto domain
        var sessionLogger = _loggerFactory.CreateLogger<DoubleRatchetSession>();
        var session = DoubleRatchetSession.AsInitiator(
            sharedSecret,
            remoteIdentityKey,
            remoteRatchetKey,
            localEphemeralKey,
            sessionLogger,
            _cryptographyOptions);

        var sessionId = new SessionId(conversationId.Value);
        _logger.LogInformation("Establish session as initiator for conversation {ConversationId}. SessionId: {SessionId}", conversationId, sessionId);
        
        // Get state and store it
        var state = session.GetState();
        
        // Log state properties to verify consistency
        if (state.RootKey != null)
        {
            if (_cryptographyOptions.Value.EnableCryptographicMaterialLogging)
            {
                _logger.LogInformation("Initiator session root key: {RootKey}", 
                    Convert.ToBase64String(state.RootKey.Value));
            }
        }
        
        if (state.TheirDhRatchetPublicKey != null)
        {
            if (_cryptographyOptions.Value.EnableCryptographicMaterialLogging)
            {
                _logger.LogInformation("Initiator session stored remote ratchet key : {StoredRatchetKey}", 
                    Convert.ToBase64String(state.TheirDhRatchetPublicKey.Value));
            }
        }
        
        await _sessionStore.SetSessionStateAsync(sessionId, state);
        _sessionLocks.TryAdd(conversationId, new SemaphoreSlim(1, 1));
    }

    public async Task EstablishSessionAsResponderAsync(
        SessionId conversationId, 
        Percolator.Identity.PeerId remotePeerId, 
        RatchetIdentityKey remoteIdentityKey,
        RatchetEphemeralKey remoteRatchetPublicKey,
        ECDiffieHellman privateKeyUsedInHandshake,
        SharedSecret sharedSecret)
    {
        if (_cryptographyOptions.Value.EnableCryptographicMaterialLogging)
        {
            _logger.LogInformation("Responder establishing session with provided key : {LocalRatchetKey}, shared secret : {SharedSecret}", 
                Convert.ToBase64String(privateKeyUsedInHandshake.PublicKey.ExportSubjectPublicKeyInfo()),
                Convert.ToBase64String(sharedSecret.Value));
        }

        // Create the session directly in Crypto domain
        var sessionLogger = _loggerFactory.CreateLogger<DoubleRatchetSession>();
        var session = DoubleRatchetSession.AsResponder(
            sharedSecret,
            remoteIdentityKey,
            remoteRatchetPublicKey,
            privateKeyUsedInHandshake,
            sessionLogger,
            _cryptographyOptions);

        var sessionId = new SessionId(conversationId.Value);
        _logger.LogInformation("Establish session as responder for conversation {ConversationId}. SessionId: {SessionId}", conversationId, sessionId);
        
        // Get state and store it
        var state = session.GetState();
        
        // Log state properties to verify consistency
        if (state.RootKey != null && _cryptographyOptions.Value.EnableCryptographicMaterialLogging)
        {
            _logger.LogInformation("Responder session root key : {RootKey}", 
                Convert.ToBase64String(state.RootKey.Value));
        }
        
        if (state.DhRatchetPrivateKey != null && _cryptographyOptions.Value.EnableCryptographicMaterialLogging)
        {
            _logger.LogInformation("Responder session stored local ratchet private key hash: {StoredRatchetPrivateKeyHash}", 
                Convert.ToBase64String(state.DhRatchetPrivateKey.Value));
        }
        
        await _sessionStore.SetSessionStateAsync(sessionId, state);
        _sessionLocks.TryAdd(conversationId, new SemaphoreSlim(1, 1));
    }

    public async Task<Plaintext?> ReceiveMessageAsync(
        SessionId conversationId, 
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
            //todo: remove unused
            var remotePeerId = await GetRemotePeerIdFromDirectMessage(conversation);

            //todo: this seems circular. do we need to create this session id?
            var sessionId = new SessionId(conversationId.Value);
            _logger.LogInformation("Receive message for conversation {ConversationId}. SessionId: {SessionId}", conversationId, sessionId);

            var sessionState = await _sessionStore.GetSessionStateAsync(sessionId);
            if (sessionState == null)
            {
                throw new InvalidOperationException($"Double Ratchet session state for conversation {conversationId} not found.");
            }

            if (_cryptographyOptions.Value.EnableCryptographicMaterialLogging)
            {
                var rootKey = sessionState.RootKey != null 
                    ? Convert.ToBase64String(sessionState.RootKey.Value) 
                    : "null";
                _logger.LogInformation("Receiver using session {SessionId} - RootKey hash: {RootKeyHash}", 
                    sessionId, rootKey);
            }
            
            // Use Crypto domain directly
            var sessionLogger = _loggerFactory.CreateLogger<DoubleRatchetSession>();
            using var session = new DoubleRatchetSession(sessionState, sessionLogger, _cryptographyOptions);
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

    public async Task<(Percolator.Identity.PeerId remotePeerId, SessionRatchetMessage encryptedMessage)> EncryptMessageAsync(
        SessionId conversationId, 
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
                throw new InvalidOperationException($"Double Ratchet session state for conversation {conversationId} not found.");
            }
            
            if (_cryptographyOptions.Value.EnableCryptographicMaterialLogging)
            {
                var rootKey = sessionState.RootKey != null 
                    ? Convert.ToBase64String(sessionState.RootKey.Value)
                    : "null";
                _logger.LogTrace("Encryptor using session {SessionId} - RootKey hash: {RootKeyHash}", 
                    sessionId, rootKey);
            }
            
            // Use Crypto domain directly
            var sessionLogger = _loggerFactory.CreateLogger<DoubleRatchetSession>();
            using var session = new DoubleRatchetSession(sessionState, sessionLogger, _cryptographyOptions);
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

    public async Task<PeerId> GetRemotePeerIdFromDirectMessage(SessionId sessionId)
    {
        var conversation = await _conversationRepository.GetByIdAsync(new Chat.ValueObjects.ConversationId(sessionId.Value));
        if (conversation is null)
            throw new InvalidOperationException($"Conversation with id {sessionId} not found");
        return await GetRemotePeerIdFromDirectMessage(conversation);
    }

    private async Task<Percolator.Identity.PeerId> GetRemotePeerIdFromDirectMessage(Conversation conversation)
    {
        
        if (_activeIdentityContext.Identity is null)
            throw new InvalidOperationException("Identity context not loaded");

        var localPeerId = _activeIdentityContext.Identity.Id;
        var remotePeerId = conversation.Participants.First(p => p.Value != localPeerId);
        return new Percolator.Identity.PeerId(remotePeerId.Value);
    }
}
