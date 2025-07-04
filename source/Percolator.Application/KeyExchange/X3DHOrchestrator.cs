using System.Security.Cryptography;
using Google.Protobuf;
using Percolator.Application.Identity;
using Percolator.Contracts;
using Percolator.Cryptography;
using Percolator.Identity.Model;
using Percolator.Sessions;
using ContractsPreKeyBundle = Percolator.Contracts.PreKeyBundle;
using CryptographyPreKeyBundle = Percolator.Cryptography.PreKeyBundle;
using SessionPeerId = Percolator.Sessions.PeerId;

namespace Percolator.Application.KeyExchange;

public class X3DHOrchestrator
{
    private readonly ActiveIdentityContext _activeIdentityContext;
    private readonly IX3DHManager _x3DhManager;

    public X3DHOrchestrator(
        ActiveIdentityContext activeIdentityContext,
        IX3DHManager x3DhManager)
    {
        _activeIdentityContext = activeIdentityContext;
        _x3DhManager = x3DhManager;
    }

    public SharedSecret CompleteHandshake(ContractsPreKeyBundle remotePreKeyBundle, ECDiffieHellman ephemeralKey)
    {
        if (_activeIdentityContext.Keys is null)
        {
            throw new InvalidOperationException("Active identity does not have keys loaded.");
        }

        // Step 1: Verify the signature on the signed pre-key.
        if (!_x3DhManager.VerifySignature(remotePreKeyBundle.IdentitySigningKey.ToByteArray(), remotePreKeyBundle.SignedPreKey.ToByteArray(), remotePreKeyBundle.PreKeySignature.ToByteArray()))
        {
            throw new CryptographicException("Invalid signature on signed pre-key.");
        }

        var remoteCryptoBundle = new CryptographyPreKeyBundle(
            remotePreKeyBundle.IdentityAgreementKey.ToByteArray(),
            remotePreKeyBundle.IdentitySigningKey.ToByteArray(),
            remotePreKeyBundle.SignedPreKey.ToByteArray(),
            remotePreKeyBundle.PreKeySignature.ToByteArray(),
            remotePreKeyBundle.OneTimePreKey?.ToByteArray()
        );

        var sharedSecret = _x3DhManager.InitiateHandshake(
            remoteCryptoBundle, 
            ephemeralKey, 
            _activeIdentityContext.Keys.IdentitySigningKey, 
            _activeIdentityContext.Keys.IdentityAgreementKey);

        return sharedSecret;
    }

    public OrchestratorResponseResult ProcessHandshake(ContractsPreKeyBundle remotePreKeyBundle, byte[] remoteEphemeralPublicKey)
    {
        if (_activeIdentityContext.Keys is not
            {
                IdentitySigningKey: var identitySigningKey,
                IdentityAgreementKey: var identityAgreementKey,
                SignedPreKey: var signedPreKey,
                OneTimePreKeys: var oneTimePreKeys
            })
        {
            throw new InvalidOperationException("Active identity is not fully initialized for X3DH handshake.");
        }

        var oneTimePreKey = oneTimePreKeys.FirstOrDefault();

        var sharedSecret = _x3DhManager.RespondToHandshake(
            remotePreKeyBundle.IdentityAgreementKey.ToByteArray(),
            remoteEphemeralPublicKey,
            identitySigningKey,
            identityAgreementKey,
            signedPreKey,
            oneTimePreKey);

        var signedPreKeyPublicBytes = signedPreKey.PublicKey.ExportSubjectPublicKeyInfo();
        var signature = _x3DhManager.SignPreKey(identitySigningKey, signedPreKeyPublicBytes);

        var responderBundle = new ContractsPreKeyBundle
        {
            IdentityAgreementKey = ByteString.CopyFrom(identityAgreementKey.PublicKey.ExportSubjectPublicKeyInfo()),
            IdentitySigningKey = ByteString.CopyFrom(identitySigningKey.ExportSubjectPublicKeyInfo()),
            SignedPreKey = ByteString.CopyFrom(signedPreKeyPublicBytes),
            PreKeySignature = ByteString.CopyFrom(signature)
        };

        if (oneTimePreKey is not null)
        {
            responderBundle.OneTimePreKey = ByteString.CopyFrom(oneTimePreKey.PublicKey.ExportSubjectPublicKeyInfo());
        }

        return new OrchestratorResponseResult(sharedSecret, responderBundle);
    }
}