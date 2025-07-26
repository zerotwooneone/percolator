using System.Security.Cryptography;
using Google.Protobuf;
using Percolator.Application.Identity;
using Percolator.Cryptography;
using Percolator.Identity;
using ContractsPreKeyBundle = Percolator.Contracts.PreKeyBundle;
using CryptographyPreKeyBundle = Percolator.Cryptography.PreKeyBundle;
using Microsoft.Extensions.Logging;

namespace Percolator.Application.KeyExchange;

public class X3DHOrchestrator : IX3DHOrchestrator
{
    private readonly ActiveIdentityContext _activeIdentityContext;
    private readonly IX3DHManager _x3DhManager;
    private readonly IOneTimeKeyProvider _oneTimeKeyProvider;
    private readonly ILogger<X3DHOrchestrator> _logger;

    public X3DHOrchestrator(
        ActiveIdentityContext activeIdentityContext,
        IX3DHManager x3DhManager,
        IOneTimeKeyProvider oneTimeKeyProvider,
        ILogger<X3DHOrchestrator> logger)
    {
        _activeIdentityContext = activeIdentityContext;
        _x3DhManager = x3DhManager;
        _oneTimeKeyProvider = oneTimeKeyProvider;
        _logger = logger;
    }

    public SharedSecret CompleteHandshake(ContractsPreKeyBundle remotePreKeyBundle, ECDiffieHellman ephemeralKey)
    {
        if (_activeIdentityContext.Keys is null)
        {
            throw new InvalidOperationException("Active identity does not have keys loaded.");
        }

        try
        {
            _logger.LogDebug("Starting handshake with remote bundle. SignedPreKey length: {Length}, IdentityKey length: {IdentityLength}",
                remotePreKeyBundle.SignedPreKey.ToByteArray().Length,
                remotePreKeyBundle.IdentityAgreementKey.ToByteArray().Length);
                
            if (remotePreKeyBundle.OneTimePreKey != null)
            {
                _logger.LogDebug("OneTimePreKey present in bundle, length: {Length}", 
                    remotePreKeyBundle.OneTimePreKey.ToByteArray().Length);
            }

            // Step 1: Verify the signature on the signed pre-key.
            if (!_x3DhManager.VerifySignature(
                    new RatchetIdentityKey(remotePreKeyBundle.IdentitySigningKey.ToByteArray()),
                    new PreKey(remotePreKeyBundle.SignedPreKey.ToByteArray()), 
                    new Signature(remotePreKeyBundle.PreKeySignature.ToByteArray())))
            {
                throw new CryptographicException("Invalid signature on signed pre-key.");
            }

            _logger.LogDebug("Signature verification successful");
            
            // Create a CryptographyPreKeyBundle with explicit null check for OneTimePreKey
            byte[]? oneTimePreKeyBytes = null;
            if (remotePreKeyBundle.OneTimePreKey != null && remotePreKeyBundle.OneTimePreKey.ToByteArray().Length > 0)
            {
                oneTimePreKeyBytes = remotePreKeyBundle.OneTimePreKey.ToByteArray();
            }

            var remoteCryptoBundle = new CryptographyPreKeyBundle(
                remotePreKeyBundle.IdentitySigningKey.ToByteArray(),
                remotePreKeyBundle.IdentityAgreementKey.ToByteArray(),
                new Signature(remotePreKeyBundle.PreKeySignature.ToByteArray()),
                remotePreKeyBundle.SignedPreKey.ToByteArray(),
                oneTimePreKeyBytes
            );

            _logger.LogDebug("Created crypto bundle, proceeding with handshake");
            
            var sharedSecret = _x3DhManager.InitiateHandshake(
                remoteCryptoBundle,
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

    public HandshakeResponse ProcessHandshake(ContractsPreKeyBundle remotePreKeyBundle, byte[] remoteEphemeralPublicKey)
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
                new RatchetIdentityKey(remotePreKeyBundle.IdentitySigningKey.ToByteArray()),
                new PreKey(remotePreKeyBundle.SignedPreKey.ToByteArray()),
                new Signature(remotePreKeyBundle.PreKeySignature.ToByteArray())))
        {
            throw new CryptographicException("Invalid signature on initiator's signed pre-key.");
        }

        var oneTimePreKey = _oneTimeKeyProvider.PopOneTimeKey();

        var sharedSecret = _x3DhManager.RespondToHandshake(
            new RatchetIdentityKey(remotePreKeyBundle.IdentityAgreementKey.ToByteArray()),
            new RatchetEphemeralKey(remoteEphemeralPublicKey),
            new PrivateAgreementKey(identityAgreementKey.ExportECPrivateKey()),
            new PrivatePreKey(signedPreKey.ExportECPrivateKey()),
            oneTimePreKey is not null ? new PrivateOneTimeKey(oneTimePreKey.ExportECPrivateKey()) : null);

        var signedPreKeyPublicBytes = signedPreKey.PublicKey.ExportSubjectPublicKeyInfo();
        var signature = _x3DhManager.SignPreKey(identitySigningKey, new PreKey(signedPreKeyPublicBytes));

        var responderBundle = new ContractsPreKeyBundle
        {
            IdentityAgreementKey = ByteString.CopyFrom(identityAgreementKey.PublicKey.ExportSubjectPublicKeyInfo()),
            IdentitySigningKey = ByteString.CopyFrom(identitySigningKey.ExportSubjectPublicKeyInfo()),
            SignedPreKey = ByteString.CopyFrom(signedPreKey.PublicKey.ExportSubjectPublicKeyInfo()),
            PreKeySignature = ByteString.CopyFrom(signature.Value)
        };

        //Do not include one-time pre-key in the response

        return new HandshakeResponse(sharedSecret, responderBundle);
    }
}