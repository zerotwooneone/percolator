using MediatR;
using Microsoft.Extensions.Logging;
using Percolator.Cryptography;
using Percolator.Identity;
using Percolator.Network;
using System.Security.Cryptography;
using CryptoSignature = Percolator.Cryptography.Signature;
using PublicKey = Percolator.Cryptography.PublicKey;

namespace Percolator.Prekey.Handlers
{
    public class SubmitPreKeyBundleHandler : IRequestHandler<SubmitPreKeyBundleCommand, Unit>
    {
        private readonly ILogger<SubmitPreKeyBundleHandler> _logger;
        private readonly Percolator.Cryptography.ISigningService _signingService;
        private readonly IPreKeyBundleRepository _bundleRepository;
        private readonly IPeerConnectionRepository _peerConnectionRepository;
        private readonly IPeerRepository _peerRepository;
        private readonly IPeerPublicSigningKeyStore _publicKeyStore;

        public SubmitPreKeyBundleHandler(
            ILogger<SubmitPreKeyBundleHandler> logger,
            Percolator.Cryptography.ISigningService signingService,
            IPreKeyBundleRepository bundleRepository,
            IPeerConnectionRepository peerConnectionRepository,
            IPeerRepository peerRepository,
            IPeerPublicSigningKeyStore publicKeyStore)
        {
            _logger = logger;
            _signingService = signingService;
            _bundleRepository = bundleRepository;
            _peerConnectionRepository = peerConnectionRepository;
            _peerRepository = peerRepository;
            _publicKeyStore = publicKeyStore;
        }

        public async Task<Unit> Handle(SubmitPreKeyBundleCommand request, CancellationToken cancellationToken)
        {
            var signedPreKeyBytes = request.SignedPreKey;
            var signedPreKeySignature = new CryptoSignature(request.PreKeySignature);
            if (!_signingService.Verify(signedPreKeyBytes, signedPreKeySignature, new PublicKey(request.PublicSigningKey)))
            {
                throw new InvalidOperationException("Invalid signed pre-key signature.");
            }

            var remoteIdentitySigningKeyBytes = request.PublicSigningKey;
            var networkIdentitySigningKey = new DirectMessagePublicKey(remoteIdentitySigningKeyBytes);
            var connectionInfo = await _peerConnectionRepository.GetByDirectMessage(networkIdentitySigningKey);
            if (connectionInfo is null)
            {
                throw new InvalidOperationException("Peer connection info not found.");
            }

            var peer = await _peerRepository.GetByIdAsync(new Percolator.Identity.PeerId(connectionInfo.Id.Value));
            if (peer is null)
            {
                throw new InvalidOperationException("Could not determine peer from signing key.");
            }

            var publicKeyHash = SHA256.HashData(remoteIdentitySigningKeyBytes);
            var nowTimestamp = DateTimeOffset.UtcNow;
            await _publicKeyStore.ActivateIfChangedAsync(peer.Id, remoteIdentitySigningKeyBytes, publicKeyHash, nowTimestamp, cancellationToken);

            var signedPreKeyId = request.SignedPreKeyId;
            var domainBundles = new List<Percolator.Cryptography.PreKeyBundle>();
            foreach (var oneTimePreKey in request.OneTimePreKeys)
            {
                var oneTimePreKeyId = oneTimePreKey.Id;

                var domainBundle = new Percolator.Cryptography.PreKeyBundle(
                    new RatchetIdentityKey(request.PublicSigningKey),
                    signedPreKeyId,
                    new PreKey(signedPreKeyBytes),
                    signedPreKeySignature,
                    oneTimePreKeyId,
                    new OneTimeKey(oneTimePreKey.Key),
                    request.Expires
                );
                domainBundles.Add(domainBundle);
            }

            if (domainBundles.Count == 0)
            {
                throw new InvalidOperationException("No valid pre-key bundles provided.");
            }

            await _bundleRepository.StoreBundlesAsync(new Percolator.Cryptography.Primitives.PeerId(peer.Id.Value), domainBundles);
            _logger.LogInformation("Stored {Count} pre-key bundles for peer {PeerId}", domainBundles.Count, peer.Id);
            return Unit.Value;
        }
    }
}
