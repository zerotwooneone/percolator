using MediatR;
using Microsoft.Extensions.Logging;
using CryptoPeerId = Percolator.Cryptography.Primitives.PeerId;
using Percolator.Cryptography;
using Percolator.Identity;
using PublicKey = Percolator.Cryptography.PublicKey;

namespace Percolator.Prekey.Handlers
{
    public class SubmitPreKeyBundleHandler : IRequestHandler<SubmitPreKeyBundleCommand, Unit>
    {
        private readonly ILogger<SubmitPreKeyBundleHandler> _logger;
        private readonly Percolator.Cryptography.ISigningService _signingService;
        private readonly IPreKeyBundleRepository _bundleRepository;
        private readonly IPeerPublicSigningKeyStore _publicKeyStore;

        public SubmitPreKeyBundleHandler(
            ILogger<SubmitPreKeyBundleHandler> logger,
            Percolator.Cryptography.ISigningService signingService,
            IPreKeyBundleRepository bundleRepository,
            IPeerPublicSigningKeyStore publicKeyStore)
        {
            _logger = logger;
            _signingService = signingService;
            _bundleRepository = bundleRepository;
            _publicKeyStore = publicKeyStore;
        }

        public async Task<Unit> Handle(SubmitPreKeyBundleCommand request, CancellationToken cancellationToken)
        {
            var signedPreKeyBytes = request.SignedPreKey;
            var signedPreKeySignature = Signature.FromBytes(request.PreKeySignature);
            if (!_signingService.Verify(signedPreKeyBytes, signedPreKeySignature, PublicKey.FromBytes(request.PublicSigningKey)))
            {
                throw new InvalidOperationException("Invalid signed pre-key signature.");
            }

            var remoteIdentitySigningKeyBytes = request.PublicSigningKey;

            var identityPeerId = new Percolator.Identity.PeerId(request.RemoteNetworkPeerId.Value);
            var nowTimestamp = DateTimeOffset.UtcNow;
            await _publicKeyStore.ActivateIfChangedAsync(identityPeerId, remoteIdentitySigningKeyBytes, nowTimestamp, cancellationToken);

            var signedPreKeyId = request.SignedPreKeyId;
            var domainBundles = new List<Percolator.Cryptography.PreKeyBundle>();
            foreach (var oneTimePreKey in request.OneTimePreKeys)
            {
                var oneTimePreKeyId = oneTimePreKey.Id;

                var domainBundle = new Percolator.Cryptography.PreKeyBundle(
                    RatchetIdentityKey.FromBytes(request.PublicSigningKey),
                    signedPreKeyId,
                    PreKey.FromBytes(signedPreKeyBytes),
                    signedPreKeySignature,
                    oneTimePreKeyId,
                    OneTimeKey.FromBytesOwned(oneTimePreKey.Key),
                    request.Expires
                );
                domainBundles.Add(domainBundle);
            }

            if (domainBundles.Count == 0)
            {
                throw new InvalidOperationException("No valid pre-key bundles provided.");
            }

            await _bundleRepository.StoreBundlesAsync(new CryptoPeerId(identityPeerId.Value), domainBundles);
            _logger.LogInformation("Stored {Count} pre-key bundles for peer {PeerId}", domainBundles.Count, request.RemoteNetworkPeerId);
            return Unit.Value;
        }
    }
}
