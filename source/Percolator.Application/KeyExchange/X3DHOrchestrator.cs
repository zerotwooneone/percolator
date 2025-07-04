using System.Security.Cryptography;
using Google.Protobuf;
using Percolator.Application.Identity;
using Percolator.Cryptography;
using Percolator.Sessions;
using ContractsPreKeyBundle = Percolator.Contracts.PreKeyBundle;
using CryptographyPreKeyBundle = Percolator.Cryptography.PreKeyBundle;
using SessionPeerId = Percolator.Sessions.PeerId;

namespace Percolator.Application.KeyExchange;

public class X3DHOrchestrator
{
    private readonly ActiveIdentityContext _activeIdentityContext;
    private readonly IX3DHManager _x3DhManager;

    public X3DHOrchestrator(ActiveIdentityContext activeIdentityContext, IX3DHManager x3DhManager)
    {
        _activeIdentityContext = activeIdentityContext;
        _x3DhManager = x3DhManager;
    }

    public OrchestratorInitiationResult InitiateHandshake(SessionPeerId remotePeerId, ContractsPreKeyBundle remotePreKeyBundle)
    {
        // Get local identity keys and validate
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

        // Translate contract DTO to cryptography domain object
        var remoteCryptoBundle = new CryptographyPreKeyBundle(
            remotePreKeyBundle.IdentityKey.ToByteArray(),
            remotePreKeyBundle.SignedPreKey.ToByteArray(),
            remotePreKeyBundle.PreKeySignature.ToByteArray(),
            remotePreKeyBundle.OneTimePreKey.ToByteArray()
        );

        // Perform X3DH handshake as initiator
        var handshakeResult = _x3DhManager.InitiateHandshake(
            remoteCryptoBundle,
            identitySigningKey,
            identityAgreementKey
        );

        // The initial ratchet public key for the Double Ratchet session is the initiator's ephemeral public key
        var initialRatchetPublicKey = new OpaquePublicKey(handshakeResult.EphemeralPublicKey.Value);

        // Create and sign the local pre-key bundle for the remote peer
        var signedPreKeyBytes = signedPreKey.PublicKey.ExportSubjectPublicKeyInfo();
        var signature = identitySigningKey.SignData(signedPreKeyBytes, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

        var localPreKeyBundle = new ContractsPreKeyBundle
        {
            IdentityKey = ByteString.CopyFrom(identityAgreementKey.PublicKey.ExportSubjectPublicKeyInfo()),
            SignedPreKey = ByteString.CopyFrom(signedPreKeyBytes),
            PreKeySignature = ByteString.CopyFrom(signature),
            OneTimePreKey = ByteString.CopyFrom(oneTimePreKeys.First().PublicKey.ExportSubjectPublicKeyInfo())
        };

        return new OrchestratorInitiationResult(handshakeResult.SharedSecret, initialRatchetPublicKey, localPreKeyBundle);
    }

    public OrchestratorResponseResult ProcessHandshake(SessionPeerId remotePeerId, ContractsPreKeyBundle localPreKeyBundle, byte[] remoteEphemeralPublicKey)
    {
        // Get local identity keys and validate
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

        var remoteEphemeralKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        remoteEphemeralKey.ImportSubjectPublicKeyInfo(remoteEphemeralPublicKey, out _);

        var remoteIdentityKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        remoteIdentityKey.ImportSubjectPublicKeyInfo(localPreKeyBundle.IdentityKey.ToByteArray(), out _);

        var sharedSecret = _x3DhManager.RespondToHandshake(
            remoteIdentityKey.ExportSubjectPublicKeyInfo(),
            remoteEphemeralKey.ExportSubjectPublicKeyInfo(),
            identitySigningKey,
            identityAgreementKey,
            signedPreKey,
            oneTimePreKeys.First()
        );

        // The initial ratchet public key for the Double Ratchet session is the responder's signed pre-key
        var initialRatchetPublicKey = new OpaquePublicKey(signedPreKey.PublicKey.ExportSubjectPublicKeyInfo());

        return new OrchestratorResponseResult(sharedSecret, initialRatchetPublicKey);
    }
}