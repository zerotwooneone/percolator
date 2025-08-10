using System.Security.Cryptography;
using Percolator.Application.Identity;
using Percolator.Cryptography;
using Microsoft.Extensions.Logging;

namespace Percolator.Application.KeyExchange;

public class X3DHOrchestrator : IX3DHOrchestrator
{
    private readonly ActiveIdentityContext _activeIdentityContext;
    private readonly IX3DHManager _x3DhManager;
    private readonly ILogger<X3DHOrchestrator> _logger;

    public X3DHOrchestrator(
        ActiveIdentityContext activeIdentityContext,
        IX3DHManager x3DhManager,
        ILogger<X3DHOrchestrator> logger)
    {
        _activeIdentityContext = activeIdentityContext;
        _x3DhManager = x3DhManager;
        _logger = logger;
    }

    public SharedSecret InitiateHandshake(X3dPreKeyBundle remotePreKeyBundle, ECDiffieHellman ephemeralKey)
    {
        if (_activeIdentityContext.Keys is null)
        {
            throw new InvalidOperationException("Active identity does not have keys loaded.");
        }

        try
        {
            _logger.LogDebug("Starting handshake with remote bundle. SignedPreKey length: {Length}, IdentityKey length: {IdentityLength}",
                remotePreKeyBundle.SignedPreKey.Value.Length,
                remotePreKeyBundle.IdentityAgreementKey.Value.Length);
                
            if (remotePreKeyBundle.OneTimePreKey != null)
            {
                _logger.LogDebug("OneTimePreKey present in bundle, length: {Length}", 
                    remotePreKeyBundle.OneTimePreKey.Value.Length);
            }

            // Step 1: Verify the signature on the signed pre-key.
            if (!_x3DhManager.VerifySignature(
                    remotePreKeyBundle.IdentitySigningKey,
                    remotePreKeyBundle.SignedPreKey, 
                    remotePreKeyBundle.PreKeySignature))
            {
                throw new CryptographicException("Invalid signature on signed pre-key.");
            }

            _logger.LogDebug("Signature verification successful");
            
            var sharedSecret = _x3DhManager.InitiateHandshake(
                remotePreKeyBundle,
                ephemeralKey,
                _activeIdentityContext.Keys.IdentityAgreementKey);

            _logger.LogDebug("Handshake completed successfully");
            return sharedSecret;
        }
        catch (CryptographicException ex)
        {
            // Log detailed exception information
            _logger.LogError(ex, "Crypto error during handshake: {Message}, Inner: {Inner}", 
                ex.Message, ex.InnerException?.Message);
                
            // Catch exceptions from malformed keys or invalid signatures to prevent DoS.
            throw new CryptographicException("Handshake failed due to an invalid pre-key bundle or signature.", ex);
        }
    }

    public HandshakeResponse CompleteHandshake(
        X3dPreKeyBundle remotePreKeyBundle, 
        byte[] remoteEphemeralPublicKey,
        ECDiffieHellman? localOneTimePreKey)
    {
        if (_activeIdentityContext.Keys is not
            {
                IdentitySigningKey: var identitySigningKey,
                IdentityAgreementKey: var identityAgreementKey,
                SignedPreKey: var signedPreKey
            })
        {
            throw new InvalidOperationException("Active identity is not fully initialized for X3DH handshake.");
        }

        // CRITICAL: Verify the signature on the initiator's signed pre-key to prevent MITM attacks.
        if (!_x3DhManager.VerifySignature(
                remotePreKeyBundle.IdentitySigningKey,
                remotePreKeyBundle.SignedPreKey,
                remotePreKeyBundle.PreKeySignature))
        {
            throw new CryptographicException("Invalid signature on initiator's signed pre-key.");
        }

        // Store which private key is actually used in handshake
        ECDiffieHellman keyUsedInHandshake;
        
        var oneTimePreKey = localOneTimePreKey;
        
        // If a one-time key is available and used, that's the key we need to track
        if (oneTimePreKey != null)
        {
            _logger.LogDebug("Using OneTimePreKey in handshake");
            keyUsedInHandshake = oneTimePreKey;
        }
        else
        {
            // Otherwise, the SignedPreKey is used
            _logger.LogDebug("Using SignedPreKey in handshake");
            keyUsedInHandshake = signedPreKey;
        }

        //todo: figure out if this agreement -> identity switch is a problem
        var sharedSecret = _x3DhManager.RespondToHandshake(
            new RatchetIdentityKey(remotePreKeyBundle.IdentityAgreementKey.Value),
            new RatchetEphemeralKey(remoteEphemeralPublicKey),
            new PrivateAgreementKey(identityAgreementKey.ExportECPrivateKey()),
            new PrivatePreKey(signedPreKey.ExportECPrivateKey()),
            oneTimePreKey is not null ? new PrivateOneTimeKey(oneTimePreKey.ExportECPrivateKey()) : null);

        var oneTimeKey = oneTimePreKey == null
            ? null
            : new OneTimeKey(oneTimePreKey.PublicKey.ExportSubjectPublicKeyInfo());
        var responderBundle = new X3dPreKeyBundle(
                new RatchetIdentityKey(identitySigningKey.ExportSubjectPublicKeyInfo()),
                new RatchetAgreementKey(identityAgreementKey.PublicKey.ExportSubjectPublicKeyInfo()),
                new PreKey(signedPreKey.PublicKey.ExportSubjectPublicKeyInfo()),
                null!,
                oneTimeKey
            );

        // Return which key was actually used in the handshake
        return new HandshakeResponse(sharedSecret, responderBundle, keyUsedInHandshake);
    }
}