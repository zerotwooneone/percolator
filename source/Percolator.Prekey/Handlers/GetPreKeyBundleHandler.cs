using MediatR;
using Microsoft.Extensions.Logging;
using Percolator.Cryptography;
using CryptoPeerId = Percolator.Cryptography.Primitives.PeerId;
using Percolator.Identity;

namespace Percolator.Prekey.Handlers
{
    public class GetPreKeyBundleHandler : IRequestHandler<GetPreKeyBundleQuery, PreKeyBundle?>
    {
        private readonly ILogger<GetPreKeyBundleHandler> _logger;
        private readonly IPeerPublicSigningKeyStore _publicKeyStore;
        private readonly IPreKeyBundleRepository _bundleRepository;

        public GetPreKeyBundleHandler(
            ILogger<GetPreKeyBundleHandler> logger,
            IPeerPublicSigningKeyStore publicKeyStore,
            IPreKeyBundleRepository bundleRepository)
        {
            _logger = logger;
            _publicKeyStore = publicKeyStore;
            _bundleRepository = bundleRepository;
        }

        public async Task<PreKeyBundle?> Handle(GetPreKeyBundleQuery request, CancellationToken cancellationToken)
        {
            var peerId = await _publicKeyStore.GetPeerIdByPublicKeyHashAsync(request.TargetPublicSigningKeyHash, cancellationToken);
            if (peerId is null)
            {
                _logger.LogWarning("No peer found for provided public signing key hash.");
                return null;
            }

            var bundle = await _bundleRepository.PopBundleAsync(new CryptoPeerId(peerId.Value));
            if (bundle is null)
            {
                _logger.LogInformation("No pre-key bundle available for peer {PeerId}", peerId);
                return null;
            }

            _logger.LogInformation("Popped pre-key bundle for peer {PeerId}", peerId);
            return bundle;
        }
    }
}
