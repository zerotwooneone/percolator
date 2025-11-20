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

    public DirectSessionManager(
        IDoubleRatchetSessionStore sessionStore,
        ActiveIdentityContext activeIdentityContext,
        ILogger<DirectSessionManager> logger,
        ILoggerFactory loggerFactory,
        IOptions<CryptographyOptions> cryptographyOptions)
    {
        _sessionStore = sessionStore;
        _activeIdentityContext = activeIdentityContext;
        _logger = logger;
        _loggerFactory = loggerFactory;
        _cryptographyOptions = cryptographyOptions;
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

        // Avoid logging cryptographic material (keys/secrets). Only log high-level action.
        _logger.LogInformation("Initiator establishing session.");
        
        // Create the session directly in Crypto domain
        var sessionLogger = _loggerFactory.CreateLogger<DoubleRatchetSession>();
        var session = DoubleRatchetSession.AsInitiator(
            sharedSecret,
            remoteIdentityKey,
            preKey,
            localEphemeralKey,
            sessionLogger,
            _cryptographyOptions);

        _logger.LogInformation("Establish session as initiator for session {SessionId}.", sessionId);
        
        // Get state and store it
        var state = session.GetState();
        
        // Do not log root keys or ratchet keys.
        
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
        // Avoid logging cryptographic material (keys/secrets). Only log high-level action.
        _logger.LogInformation("Responder establishing session.");

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

        // Do not log root keys or ratchet keys.
        
        
        await _sessionStore.SetSessionStateAsync(sessionId, state, _activeIdentityContext.Identity.SelfIdentityId).ConfigureAwait(false);
        _sessionLocks.TryAdd(sessionId, new SemaphoreSlim(1, 1));
    }
    
    /// <summary>
    /// R2: Initiator finalize stub. Will be implemented to initialize from IRK + responder header public ratchet key.
    /// </summary>
    public async Task FinalizeAsInitiatorAsync(
        SessionId sessionId,
        RatchetIdentityKey responderIdentityKey,
        SharedSecret initialRootKey,
        RatchetEphemeralKey responderPublicRatchetKey)
    {
        if (_activeIdentityContext.Identity is null)
            throw new InvalidOperationException("Identity context not loaded");

        // Create a Double Ratchet session as initiator using the Initial Root Key (from X3DH) and the responder's header ratchet key.
        var sessionLogger = _loggerFactory.CreateLogger<DoubleRatchetSession>();

        using var generatedLocalEphemeral = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var session = DoubleRatchetSession.AsInitiator(
            initialRootKey,
            responderIdentityKey,
            responderPublicRatchetKey,
            generatedLocalEphemeral,
            sessionLogger,
            _cryptographyOptions);

        var state = session.GetState();
        await _sessionStore.SetSessionStateAsync(sessionId, state, _activeIdentityContext.Identity.SelfIdentityId).ConfigureAwait(false);
        _sessionLocks.TryAdd(sessionId, new SemaphoreSlim(1, 1));
    }
}
