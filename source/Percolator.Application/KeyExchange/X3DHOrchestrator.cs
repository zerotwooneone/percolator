using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using Google.Protobuf;
using Percolator.Application.Identity;
using Percolator.Contracts;
using Percolator.Cryptography;
using ContractsPreKeyBundle = Percolator.Contracts.PreKeyBundle;
using CryptographyPreKeyBundle = Percolator.Cryptography.PreKeyBundle;
using SessionPeerId = Percolator.Sessions.PeerId;

namespace Percolator.Application.KeyExchange
{
    public class X3DHOrchestrator
    {
        private readonly ActiveIdentityContext _activeIdentityContext;
        private readonly X3DHManager _x3DhManager;

        public X3DHOrchestrator(ActiveIdentityContext activeIdentityContext, X3DHManager x3DhManager)
        {
            _activeIdentityContext = activeIdentityContext;
            _x3DhManager = x3DhManager;
        }

        public async Task<(byte[] sharedSecret, byte[] initialRatchetPublicKey, ContractsPreKeyBundle localPreKeyBundle, byte[] ephemeralPublicKey)> InitiateHandshakeAsync(SessionPeerId remotePeerId, ContractsPreKeyBundle remotePreKeyBundle)
        {
            // Get local identity keys
            var activeIdentity = _activeIdentityContext;
            if (activeIdentity.Certificate == null || activeIdentity.X3dhKeys == null)
            {
                throw new InvalidOperationException("Active identity is not fully initialized for X3DH handshake.");
            }

            // Generate ephemeral keys for the initiator
            using var ephemeralKeyPair = System.Security.Cryptography.ECDiffieHellman.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);

            // Perform X3DH handshake as initiator
            var sharedSecret = _x3DhManager.InitiateHandshake(
                new CryptographyPreKeyBundle(
                    remotePreKeyBundle.IdentityKey.ToByteArray(),
                    remotePreKeyBundle.SignedPreKey.ToByteArray(),
                    remotePreKeyBundle.PreKeySignature.ToByteArray(),
                    remotePreKeyBundle.OneTimePreKey.ToByteArray()
                ),
                activeIdentity.Certificate.GetECDsaPrivateKey(),
                activeIdentity.X3dhKeys.IdentityAgreementKey,
                ephemeralKeyPair
            );

            // The initial ratchet public key for the Double Ratchet session is the initiator's ephemeral public key
            var initialRatchetPublicKey = ephemeralKeyPair.PublicKey.ExportSubjectPublicKeyInfo();

            // Return local pre-key bundle for the remote peer to complete their side of the handshake
            var localPreKeyBundle = new ContractsPreKeyBundle
            {
                IdentityKey = ByteString.CopyFrom(activeIdentity.X3dhKeys.IdentityAgreementKey.PublicKey.ExportSubjectPublicKeyInfo()),
                SignedPreKey = ByteString.CopyFrom(activeIdentity.X3dhKeys.SignedPreKey.PublicKey.ExportSubjectPublicKeyInfo()),
                PreKeySignature = ByteString.CopyFrom(new byte[0]), // Signature is not implemented yet
                OneTimePreKey = ByteString.CopyFrom(activeIdentity.X3dhKeys.OneTimePreKey.PublicKey.ExportSubjectPublicKeyInfo())
            };

            return (sharedSecret, initialRatchetPublicKey, localPreKeyBundle, ephemeralKeyPair.PublicKey.ExportSubjectPublicKeyInfo());
        }

        public async Task<(byte[] sharedSecret, byte[] initialRatchetPublicKey)> ProcessHandshakeAsync(SessionPeerId remotePeerId, ContractsPreKeyBundle localPreKeyBundle, byte[] remoteEphemeralPublicKey)
        {
            // Get local identity keys
            var activeIdentity = _activeIdentityContext;
            if (activeIdentity.Certificate == null || activeIdentity.X3dhKeys == null)
            {
                throw new InvalidOperationException("Active identity is not fully initialized for X3DH handshake.");
            }

            // Perform X3DH handshake as responder
            var sharedSecret = _x3DhManager.RespondToHandshake(
                new CryptographyPreKeyBundle(
                    localPreKeyBundle.IdentityKey.ToByteArray(),
                    localPreKeyBundle.SignedPreKey.ToByteArray(),
                    localPreKeyBundle.PreKeySignature.ToByteArray(),
                    localPreKeyBundle.OneTimePreKey.ToByteArray()
                ),
                activeIdentity.Certificate.GetECDsaPrivateKey(),
                activeIdentity.X3dhKeys.IdentityAgreementKey,
                activeIdentity.X3dhKeys.SignedPreKey,
                activeIdentity.X3dhKeys.OneTimePreKey
            );

            // The initial ratchet public key for the Double Ratchet session is the responder's signed pre-key
            var initialRatchetPublicKey = activeIdentity.X3dhKeys.SignedPreKey.PublicKey.ExportSubjectPublicKeyInfo();

            return (sharedSecret, initialRatchetPublicKey);
        }
    }
}