using Percolator.Application.Identity;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Percolator.Chat;
using Percolator.Application.Network;
using Percolator.Cryptography;
using Percolator.Identity;
using Percolator.Application.Network.Handshake;

namespace Percolator.Application.Sessions;

/// <summary>
/// Manages Double Ratchet sessions for direct messaging.
/// Provides two establishment flows:
/// - Direct: caller supplies a <see cref="SessionId"/> and keys to create sessions on both sides.
/// - Prehandshake: caller saves an initiator intent and later completes it on responder hello.
/// Thread safety: per-session concurrency is controlled via an internal semaphore per <see cref="SessionId"/>.
/// </summary>
public class DirectSessionManager : IDirectSessionManager
{
    private readonly IDoubleRatchetSessionStore _sessionStore;
    private readonly ActiveIdentityContext _activeIdentityContext;
    private readonly ILogger<DirectSessionManager> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IOptions<CryptographyOptions> _cryptographyOptions;
    private readonly ConcurrentDictionary<SessionId, SemaphoreSlim> _sessionLocks = new();
    private readonly IRatchetKeyIndex _ratchetLookup;
    private readonly IPreHandshakeSessionStore _preHandshakeStore;

    public DirectSessionManager(
        IDoubleRatchetSessionStore sessionStore,
        ActiveIdentityContext activeIdentityContext,
        ILogger<DirectSessionManager> logger,
        ILoggerFactory loggerFactory,
        IOptions<CryptographyOptions> cryptographyOptions,
        IRatchetKeyIndex ratchetLookup,
        IPreHandshakeSessionStore preHandshakeStore)
    {
        _sessionStore = sessionStore;
        _activeIdentityContext = activeIdentityContext;
        _logger = logger;
        _loggerFactory = loggerFactory;
        _cryptographyOptions = cryptographyOptions;
        _ratchetLookup = ratchetLookup;
        _preHandshakeStore = preHandshakeStore;
    }

    public async Task<(SessionId sessionId, Plaintext plaintext)> CompleteHandshakeAsync(
        SessionRatchetMessage encryptedMessage,
        Func<Plaintext, SessionId> getSessionId,
        CancellationToken cancellationToken)
    {
        var result = await CompleteAsync(encryptedMessage, pt => pt, getSessionId, cancellationToken).ConfigureAwait(false);
        return (result.sessionId, result.envelop);
    }

    public async Task<(SessionId sessionId, Plaintext plaintext)> EstablishSessionAsResponderAsync(
        SessionRatchetMessage firstMessage,
        Func<Plaintext, SessionId> getSessionId,
        RatchetIdentityKey remoteIdentityKey,
        RatchetEphemeralKey remotePreKey,
        ECDiffieHellman privateKeyUsedInHandshake,
        SharedSecret sharedSecret)
    {
        if (_activeIdentityContext.Identity is null)
            throw new InvalidOperationException("Identity context not loaded");

        var selfId = _activeIdentityContext.Identity.SelfIdentityId;

        // Build responder session and decrypt the first message to learn the SessionId
        var sessionLogger = _loggerFactory.CreateLogger<DoubleRatchetSession>();
        using var responder = DoubleRatchetSession.AsResponder(
            sharedSecret,
            remoteIdentityKey,
            remotePreKey,
            privateKeyUsedInHandshake,
            sessionLogger,
            _cryptographyOptions);

        var plaintext = responder.Decrypt(firstMessage);
        var sessionId = getSessionId(plaintext);

        // Persist responder session state
        var state = responder.GetState();
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
        await _sessionStore.SetSessionStateAsync(sessionId, state, selfId).ConfigureAwait(false);
        _sessionLocks.TryAdd(sessionId, new SemaphoreSlim(1, 1));

        // Warm fast-path ratchet index
        var header = firstMessage.GetHeader();
        await _ratchetLookup.UpsertAsync(
            sessionId,
            header.PreKey,
            DateTimeOffset.UtcNow,
            CancellationToken.None).ConfigureAwait(false);

        return (sessionId, plaintext);
    }

    private async Task<(SessionId sessionId, TEnvelop envelop)> CompleteAsync<TEnvelop>(
        SessionRatchetMessage encryptedMessage,
        Func<Plaintext, TEnvelop> getEnvelope,
        Func<TEnvelop, SessionId> getSessionId,
        CancellationToken cancellationToken)
    {
        var selfId = _activeIdentityContext.Identity!.SelfIdentityId;
        var header = encryptedMessage.GetHeader();

        // Fast-path: try resolve header pre-key to session
        var resolved = await _ratchetLookup.TryResolveAsync(header.PreKey, cancellationToken).ConfigureAwait(false);

        Plaintext? plaintext = null;
        SessionId sid;

        if (resolved is not null)
        {
            sid = resolved;
            plaintext = await ReceiveMessageAsync(sid, encryptedMessage).ConfigureAwait(false);
            if (plaintext is null)
                throw new InvalidOperationException("CompleteHandshakeAsync: decryption failed on fast-path session.");
        }
        else
        {
            // Slow-path: iterate Pending pre-handshake entries and attempt decrypt per candidate.
            await foreach (var record in _preHandshakeStore.EnumeratePendingAsync(selfId, cancellationToken).ConfigureAwait(false))
            {
                // Reconstruct initiator ephemeral key used during pre-handshake
                using var eph = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
                eph.ImportECPrivateKey(record.InitiatorEphemeralPrivateKey, out _);
                var remoteId = new RatchetIdentityKey(record.RemoteIdentityKeySpki!);

                var sessionLogger = _loggerFactory.CreateLogger<DoubleRatchetSession>();
                // Rehydrate session state to the exact point after initiator encryption
                var restoredState = new DoubleRatchetSession.DoubleRatchetSessionState
                {
                    RootKey = new RootKey(record.InitialRootKey),
                    RatchetFlag = false,
                    SendingChainKey = null,
                    ReceivingChainKey = null,
                    SendingCounter = 0,
                    ReceivingCounter = 0,
                    PreviousChainLength = 0,
                    // For responder hello, theirDhRatchetPublicKey will be taken from the incoming header during decrypt
                    TheirDhRatchetPublicKey = null,
                    DhRatchetPrivateKey = new PrivateEphemeralKey(record.InitiatorEphemeralPrivateKey),
                    TheirIdentityPublicKey = remoteId
                };

                using var temp = new DoubleRatchetSession(restoredState, sessionLogger, _cryptographyOptions);

                try
                {
                    var pt = temp.Decrypt(encryptedMessage);
                    var env = getEnvelope(pt);
                    var resolvedSessionId = getSessionId(env);

                    // Persist established session state for the resolved session id
                    var finalizedState = temp.GetState();
                    if (_activeIdentityContext.Identity is null)
                        throw new InvalidOperationException("Identity context not loaded");
                    await _sessionStore.SetSessionStateAsync(resolvedSessionId, finalizedState, _activeIdentityContext.Identity.SelfIdentityId).ConfigureAwait(false);
                    _sessionLocks.TryAdd(resolvedSessionId, new SemaphoreSlim(1, 1));

                    // upsert fast-path mapping and return
                    await _ratchetLookup.UpsertAsync(
                        resolvedSessionId,
                        header.PreKey,
                        DateTimeOffset.UtcNow,
                        cancellationToken).ConfigureAwait(false);

                    // delete matched pending pre-handshake record now that session is finalized
                    await _preHandshakeStore.DeleteAsync(record.Id, selfId, cancellationToken).ConfigureAwait(false);

                    return (resolvedSessionId, env);
                }
                catch (CryptographicException)
                {
                    // Not a match; continue to next candidate
                }
            }

            throw new InvalidOperationException("CompleteHandshakeAsync: unable to resolve session via fast or slow path.");
        }

        var envelope = getEnvelope(plaintext);
        var sessionId = getSessionId(envelope);

        // Upsert ratchet header key -> session mapping to keep fast-path warm
        await _ratchetLookup.UpsertAsync(
            sessionId,
            header.PreKey,
            DateTimeOffset.UtcNow,
            cancellationToken).ConfigureAwait(false);

        return (sessionId, envelope);
    }

    /// <summary>
    /// Establishes a direct initiator session for the given <see cref="SessionId"/>.
    /// Use this when both sides coordinate a session id out-of-band (no prehandshake).
    /// </summary>
    public async Task EstablishSessionAsInitiatorAsync(
        SessionId sessionId,
        RatchetIdentityKey remoteIdentityKey,
        RatchetEphemeralKey preKey,
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
        await _sessionStore.SetSessionStateAsync(sessionId, state, _activeIdentityContext.Identity.SelfIdentityId).ConfigureAwait(false);
        _sessionLocks.TryAdd(sessionId, new SemaphoreSlim(1, 1));
    }

    /// <summary>
    /// Establishes a direct responder session for the given <see cref="SessionId"/>.
    /// Use with the initiator's identity public key and ephemeral public key.
    /// </summary>
    public async Task EstablishSessionAsResponderAsync(
        SessionId sessionId, 
        RatchetIdentityKey remoteIdentityKey,
        RatchetEphemeralKey remotePreKey,
        ECDiffieHellman privateKeyUsedInHandshake,
        SharedSecret sharedSecret)
    {
        if (_activeIdentityContext.Identity is null)
            throw new InvalidOperationException("Identity context not loaded");
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
        
        
        await _sessionStore.SetSessionStateAsync(sessionId, state, _activeIdentityContext.Identity.SelfIdentityId).ConfigureAwait(false);
        _sessionLocks.TryAdd(sessionId, new SemaphoreSlim(1, 1));
    }

    /// <summary>
    /// Receives and decrypts a message for an established <see cref="SessionId"/>.
    /// Updates session state and upserts the ratchet index mapping upon success.
    /// </summary>
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
        await semaphore.WaitAsync().ConfigureAwait(false);

        try
        {
            if (_activeIdentityContext.Identity is null)
                throw new InvalidOperationException("Identity context not loaded");
            _logger.LogInformation("Receive message for SessionId: {SessionId}", sessionId);

            var sessionState = await _sessionStore.GetSessionStateAsync(sessionId, _activeIdentityContext.Identity.SelfIdentityId).ConfigureAwait(false);
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
            await _sessionStore.SetSessionStateAsync(sessionId, session.GetState(), _activeIdentityContext.Identity.SelfIdentityId).ConfigureAwait(false);

            // Centralize ratchet-key index upsert on successful decrypt
            if (decryptedPlaintext is not null)
            {
                var header = encryptedMessage.GetHeader();
                await _ratchetLookup.UpsertAsync(sessionId, header.PreKey, DateTimeOffset.UtcNow, CancellationToken.None).ConfigureAwait(false);
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

    /// <summary>
    /// Encrypts a message within an established <see cref="SessionId"/> and persists updated state.
    /// </summary>
    public async Task<SessionRatchetMessage> EncryptMessageAsync(
        SessionId sessionId, 
        Plaintext plaintext)
    {
        // Ensure only one message is processed at a time for a given session to prevent race conditions.
        var semaphore = _sessionLocks.GetOrAdd(sessionId, new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync().ConfigureAwait(false);

        try
        {
            if (_activeIdentityContext.Identity is null)
                throw new InvalidOperationException("Identity context not loaded");
            
            _logger.LogInformation("Encrypt message for SessionId: {SessionId}", sessionId);
            
            var sessionState = await _sessionStore.GetSessionStateAsync(sessionId, _activeIdentityContext.Identity.SelfIdentityId).ConfigureAwait(false);
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
            await _sessionStore.SetSessionStateAsync(sessionId, session.GetState(), _activeIdentityContext.Identity.SelfIdentityId).ConfigureAwait(false);

            return encryptedMessage;
        }
        finally
        {
            semaphore.Release();
        }
    }

    /// <summary>
    /// Attempts to infer the target session among all known sessions by trial-decrypting.
    /// Returns null if none match; on success returns the matched <see cref="SessionId"/> and plaintext.
    /// </summary>
    public async Task<(SessionId sessionId, Plaintext? plaintext)?> TryInferAndReceiveAsync(SessionRatchetMessage encryptedMessage, CancellationToken cancellationToken)
    {
        if (_activeIdentityContext.Identity is null)
            throw new InvalidOperationException("Identity context not loaded");

        var selfIdentityId = _activeIdentityContext.Identity.SelfIdentityId;
        var header = encryptedMessage.GetHeader();

        // Enumerate all known sessions for this identity and attempt trial decrypt
        var sessionIds = await _sessionStore.GetAllSessionIdsAsync(selfIdentityId).ConfigureAwait(false);
        foreach (var sid in sessionIds)
        {
            // Load state
            var state = await _sessionStore.GetSessionStateAsync(sid, selfIdentityId).ConfigureAwait(false);
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
                await _sessionStore.SetSessionStateAsync(sid, session.GetState(), selfIdentityId).ConfigureAwait(false);

                // Upsert ratchet-key index mapping to keep the fast-path fresh
                await _ratchetLookup.UpsertAsync(sid, header.PreKey, DateTimeOffset.UtcNow, CancellationToken.None).ConfigureAwait(false);

                return (sid, pt);
            }
        }

        return null;
    }

    /// <summary>
    /// Saves an initiator prehandshake intent and optionally encrypts the first message.
    /// Use with <see cref="CompleteHandshakeAsync{TEnvelop}(SessionRatchetMessage, Func{Plaintext, TEnvelop}, Func{TEnvelop, SessionId}, CancellationToken)"/> to finalize later.
    /// </summary>
    public async Task<SessionRatchetMessage?> EstablishSessionAsInitiatorAsync(
        byte[] recipientPublicKeyHash,
        Guid signedPreKeyId,
        Guid? oneTimePreKeyId,
        RatchetIdentityKey remoteIdentityKey,
        RatchetEphemeralKey remotePreKey,
        SharedSecret sharedSecret,
        ECDiffieHellman initiatorEphemeral,
        Plaintext? initialPlaintext,
        CancellationToken cancellationToken)
    {
        if (_activeIdentityContext.Identity is null)
            throw new InvalidOperationException("Identity context not loaded");


        // Build a temporary initiator session to capture initial state and optionally encrypt the first message.

        var sessionLogger = _loggerFactory.CreateLogger<DoubleRatchetSession>();
        using var temp = DoubleRatchetSession.AsInitiator(
            sharedSecret,
            remoteIdentityKey,
            remotePreKey,
            initiatorEphemeral,
            sessionLogger,
            _cryptographyOptions);

        SessionRatchetMessage? firstMessage = null;
        if (initialPlaintext is not null)
        {
            firstMessage = temp.Encrypt(initialPlaintext);
        }

        // Capture a minimal session state snapshot required for responder slow-path decryption
        var state = temp.GetState();
        if (state.DhRatchetPrivateKey is null)
        {
            throw new InvalidOperationException("Initiator pre-handshake snapshot must include DhRatchetPrivateKey for slow-path completion.");
        }

        if (state.RootKey is null)
            throw new InvalidOperationException("Initiator pre-handshake snapshot must include InitialRootKey.");

        var record = new PreHandshakeRecord(
            Id: 0,
            SelfIdentityId: _activeIdentityContext.Identity.SelfIdentityId,
            RecipientPublicKeyHash: recipientPublicKeyHash,
            LocalRequestId: Guid.NewGuid(),
            InitiatorEphemeralPrivateKey: state.DhRatchetPrivateKey.Value,
            InitialRootKey: state.RootKey.Value,
            CreatedAtUtc: DateTimeOffset.UtcNow,
            ExpiresAtUtc: null,
            RemoteIdentityKeySpki: remoteIdentityKey.Value);

        await _preHandshakeStore.SaveAsync(record, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Saved initiator pre-handshake intent for recipient PKH length {Len}", recipientPublicKeyHash?.Length ?? 0);

        return firstMessage;
    }
}
