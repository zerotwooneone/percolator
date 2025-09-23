using Percolator.Application.Identity;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Percolator.Chat;
using Percolator.Application.Network;
using Percolator.Cryptography;
using Percolator.Identity;

namespace Percolator.Application.Sessions;

/// <summary>
/// Manages Double Ratchet sessions for direct messaging, working directly with cryptography primitives.
/// </summary>
public class DirectSessionManager : IDirectSessionManager
{
    private readonly IDoubleRatchetSessionStore _sessionStore;
    private readonly ActiveIdentityContext _activeIdentityContext;
    private readonly ILogger<DirectSessionManager> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IOptions<CryptographyOptions> _cryptographyOptions;
    private readonly ConcurrentDictionary<SessionId, SemaphoreSlim> _sessionLocks = new();
    private readonly IRatchetKeySessionLookup _ratchetLookup;

    public DirectSessionManager(
        IDoubleRatchetSessionStore sessionStore,
        ActiveIdentityContext activeIdentityContext,
        ILogger<DirectSessionManager> logger,
        ILoggerFactory loggerFactory,
        IOptions<CryptographyOptions> cryptographyOptions,
        IRatchetKeySessionLookup ratchetLookup)
    {
        _sessionStore = sessionStore;
        _activeIdentityContext = activeIdentityContext;
        _logger = logger;
        _loggerFactory = loggerFactory;
        _cryptographyOptions = cryptographyOptions;
        _ratchetLookup = ratchetLookup;
    }

    public async Task EstablishSessionAsInitiatorAsync(
        SessionId sessionId,
        RatchetIdentityKey remoteIdentityKey,
        PreKey preKey,
        SharedSecret sharedSecret, 
        ECDiffieHellman localEphemeralKey)
    {
        if (_activeIdentityContext.Keys is null)
            throw new InvalidOperationException("Identity context not loaded");

        if (_cryptographyOptions.Value.EnableCryptographicMaterialLogging)
        {
            _logger.LogInformation("Initiator establishing session with remote ratchet key : {RemoteRatchetKey}, shared secret : {SharedSecret}", 
                Convert.ToBase64String(preKey.Value),
                Convert.ToBase64String(sharedSecret.Value));
        }
        
        // Create the session directly in Crypto domain
        var sessionLogger = _loggerFactory.CreateLogger<DoubleRatchetSession>();
        var session = DoubleRatchetSession.AsInitiator(
            sharedSecret,
            remoteIdentityKey,
            preKey,
            localEphemeralKey,
            sessionLogger,
            _cryptographyOptions);

        _logger.LogInformation("Establish session as initiator for session {SessionId}. ", sessionId);
        
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
        
        if (_activeIdentityContext.Identity is null)
            throw new InvalidOperationException("Identity context not loaded");
        await _sessionStore.SetSessionStateAsync(sessionId, state, _activeIdentityContext.Identity.SelfIdentityId);
        _sessionLocks.TryAdd(sessionId, new SemaphoreSlim(1, 1));
    }

    public async Task EstablishSessionAsResponderAsync(
        SessionId sessionId, 
        RatchetIdentityKey remoteIdentityKey,
        PreKey remotePreKey,
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
            remotePreKey,
            privateKeyUsedInHandshake,
            sessionLogger,
            _cryptographyOptions);

        _logger.LogInformation("Establish session as responder for SessionId: {SessionId}", sessionId);
        
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
        
        if (_activeIdentityContext.Identity is null)
            throw new InvalidOperationException("Identity context not loaded");
        await _sessionStore.SetSessionStateAsync(sessionId, state, _activeIdentityContext.Identity.SelfIdentityId);
        _sessionLocks.TryAdd(sessionId, new SemaphoreSlim(1, 1));
    }

    public async Task<Plaintext?> ReceiveMessageAsync(
        SessionId sessionId, 
        SessionRatchetMessage encryptedMessage)
    {
        if (_activeIdentityContext.Identity is null || _activeIdentityContext.Keys is null)
        {
            throw new InvalidOperationException("No active identity found to receive message.");
        }

        // Ensure only one message is processed at a time for a given session to prevent race conditions.
        var semaphore = _sessionLocks.GetOrAdd(sessionId, new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync();

        try
        {
            if (_activeIdentityContext.Identity is null)
                throw new InvalidOperationException("Identity context not loaded");
            _logger.LogInformation("Receive message for SessionId: {SessionId}", sessionId);

            var sessionState = await _sessionStore.GetSessionStateAsync(sessionId, _activeIdentityContext.Identity.SelfIdentityId);
            if (sessionState == null)
            {
                throw new InvalidOperationException($"Double Ratchet session state for session {sessionId} not found.");
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
            await _sessionStore.SetSessionStateAsync(sessionId, session.GetState(), _activeIdentityContext.Identity.SelfIdentityId);

            // Centralize ratchet-key index upsert on successful decrypt
            if (decryptedPlaintext is not null)
            {
                var header = encryptedMessage.GetHeader();
                var ratchetKey = header.PreKey.Value;
                await _ratchetLookup.UpsertAsync(new Percolator.Network.DirectSessionId(sessionId.Value), _activeIdentityContext.Identity!.SelfIdentityId, ratchetKey, DateTimeOffset.UtcNow, CancellationToken.None);
            }

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

    public async Task<SessionRatchetMessage> EncryptMessageAsync(
        SessionId sessionId, 
        Plaintext plaintext)
    {
        // Ensure only one message is processed at a time for a given session to prevent race conditions.
        var semaphore = _sessionLocks.GetOrAdd(sessionId, new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync();

        try
        {
            if (_activeIdentityContext.Identity is null)
                throw new InvalidOperationException("Identity context not loaded");
            
            _logger.LogInformation("Encrypt message for SessionId: {SessionId}", sessionId);
            
            var sessionState = await _sessionStore.GetSessionStateAsync(sessionId, _activeIdentityContext.Identity.SelfIdentityId);
            if (sessionState == null)
            {
                throw new InvalidOperationException($"Double Ratchet session state for session {sessionId} not found.");
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
            await _sessionStore.SetSessionStateAsync(sessionId, session.GetState(), _activeIdentityContext.Identity.SelfIdentityId);

            return encryptedMessage;
        }
        finally
        {
            semaphore.Release();
        }
    }

    public async Task<(SessionId sessionId, Plaintext? plaintext)?> TryInferAndReceiveAsync(SessionRatchetMessage encryptedMessage, CancellationToken cancellationToken)
    {
        if (_activeIdentityContext.Identity is null)
            throw new InvalidOperationException("Identity context not loaded");

        var selfIdentityId = _activeIdentityContext.Identity.SelfIdentityId;
        var header = encryptedMessage.GetHeader();
        var ratchetKey = header.PreKey.Value;

        // Enumerate all known sessions for this identity and attempt trial decrypt
        var sessionIds = await _sessionStore.GetAllSessionIdsAsync(selfIdentityId);
        foreach (var sid in sessionIds)
        {
            // Load state
            var state = await _sessionStore.GetSessionStateAsync(sid, selfIdentityId);
            if (state is null)
            {
                continue;
            }

            var sessionLogger = _loggerFactory.CreateLogger<DoubleRatchetSession>();
            using var session = new DoubleRatchetSession(state, sessionLogger, _cryptographyOptions);

            // Trial decrypt
            Plaintext? pt = null;
            try
            {
                pt = session.Decrypt(encryptedMessage);
            }
            catch
            {
                // Any cryptographic exception means this session is not a match; continue
            }

            if (pt is not null)
            {
                // Persist updated state
                await _sessionStore.SetSessionStateAsync(sid, session.GetState(), selfIdentityId);

                // Upsert ratchet-key index mapping to keep the fast-path fresh
                await _ratchetLookup.UpsertAsync(new Percolator.Network.DirectSessionId(sid.Value), selfIdentityId, ratchetKey, DateTimeOffset.UtcNow, CancellationToken.None);

                return (sid, pt);
            }
        }

        return null;
    }
}
