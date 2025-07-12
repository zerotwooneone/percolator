using System.Security.Cryptography;
using Google.Protobuf;
using Percolator.Application.Identity;
using Percolator.Cryptography;
using Percolator.Identity;
using ContractsPreKeyBundle = Percolator.Contracts.PreKeyBundle;
using CryptographyPreKeyBundle = Percolator.Cryptography.PreKeyBundle;

namespace Percolator.Application.KeyExchange;

public class X3DHOrchestrator : IX3DHOrchestrator
{
    private readonly ActiveIdentityContext _activeIdentityContext;
    private readonly IX3DHManager _x3DhManager;
    private readonly IOneTimeKeyProvider _oneTimeKeyProvider;

    public X3DHOrchestrator(
        ActiveIdentityContext activeIdentityContext,
        IX3DHManager x3DhManager,
        IOneTimeKeyProvider oneTimeKeyProvider)
    {
        _activeIdentityContext = activeIdentityContext;
        _x3DhManager = x3DhManager;
        _oneTimeKeyProvider = oneTimeKeyProvider;
    }

    public SharedSecret CompleteHandshake(ContractsPreKeyBundle remotePreKeyBundle, ECDiffieHellman ephemeralKey)
    {
        if (_activeIdentityContext.Keys is null)
        {
            throw new InvalidOperationException("Active identity does not have keys loaded.");
        }

        try
        {
            // Step 1: Verify the signature on the signed pre-key.
            if (!_x3DhManager.VerifySignature(
                    new RatchetIdentityKey(remotePreKeyBundle.IdentitySigningKey.ToByteArray()),
                    new PreKey(remotePreKeyBundle.SignedPreKey.ToByteArray()), 
                    new Signature(remotePreKeyBundle.PreKeySignature.ToByteArray())))
            {
                throw new CryptographicException("Invalid signature on signed pre-key.");
            }

            var remoteCryptoBundle = new CryptographyPreKeyBundle(
                remotePreKeyBundle.IdentitySigningKey.ToByteArray(),
                remotePreKeyBundle.IdentityAgreementKey.ToByteArray(),
                new Signature(remotePreKeyBundle.PreKeySignature.ToByteArray()),
                remotePreKeyBundle.SignedPreKey.ToByteArray(),
                remotePreKeyBundle.OneTimePreKey?.ToByteArray()
            );

            var sharedSecret = _x3DhManager.InitiateHandshake(
                remoteCryptoBundle,
                ephemeralKey,
                _activeIdentityContext.Keys.IdentityAgreementKey);

            return sharedSecret;
        }
        catch (CryptographicException ex)
        {
            // Catch exceptions from malformed keys or invalid signatures to prevent DoS.
            throw new CryptographicException("Handshake failed due to an invalid pre-key bundle or signature.", ex);
        }
    }

    public OrchestratorResponseResult ProcessHandshake(ContractsPreKeyBundle remotePreKeyBundle, byte[] remoteEphemeralPublicKey)
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

        return new OrchestratorResponseResult(sharedSecret, responderBundle);
    }
}