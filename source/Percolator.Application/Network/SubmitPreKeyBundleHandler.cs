using Google.Protobuf;
using MediatR;
using Microsoft.Extensions.Logging;
using Percolator.Cryptography;
using Percolator.Identity;
using Percolator.Network;
using CryptoSignature = Percolator.Cryptography.Signature;
using PublicKey = Percolator.Cryptography.PublicKey;

namespace Percolator.Application.Network
{
    public class SubmitPreKeyBundleHandler : IRequestHandler<SubmitPreKeyBundleCommand, Unit>
    {
        private readonly ILogger<SubmitPreKeyBundleHandler> _logger;
        private readonly Percolator.Cryptography.ISigningService _signingService;
        private readonly IPreKeyBundleRepository _bundleRepository;
        private readonly IPeerConnectionRepository _peerConnectionRepository;
        private readonly IPeerRepository _peerRepository;

        public SubmitPreKeyBundleHandler(
            ILogger<SubmitPreKeyBundleHandler> logger,
            Percolator.Cryptography.ISigningService signingService,
            IPreKeyBundleRepository bundleRepository,
            IPeerConnectionRepository peerConnectionRepository,
            IPeerRepository peerRepository)
        {
            _logger = logger;
            _signingService = signingService;
            _bundleRepository = bundleRepository;
            _peerConnectionRepository = peerConnectionRepository;
            _peerRepository = peerRepository;
        }

        public async Task<Unit> Handle(SubmitPreKeyBundleCommand request, CancellationToken cancellationToken)
        {
            // Validate outer signature
            if (!_signingService.Verify(
                    request.SignedPayloadBytes,
                    new CryptoSignature(request.SignatureBytes),
                    new PublicKey(request.IdentityKeyBytes)))
            {
                throw new InvalidOperationException("Invalid pre-key bundle submission signature.");
            }

            var payload = Contracts.SubmitPreKeyBundleRequest.Types.PreKeyUploadPayload.Parser.ParseFrom(ByteString.CopyFrom(request.SignedPayloadBytes));
            const int maxBundles = 100;
            if (payload.OneTimePreKeys.Count == 0 || payload.OneTimePreKeys.Count > maxBundles)
            {
                throw new InvalidOperationException($"Request must contain between 1 and {maxBundles} bundles.");
            }
            if (payload.TimestampUtc == null || payload.ExpiresUtc == null)
            {
                throw new InvalidOperationException("Request must contain timestamp and expiration.");
            }

            if (!payload.SignedPreKey.HasId)
            {
                throw new InvalidOperationException("Signed pre-key must include an ID.");
            }

            var nowTimestamp = DateTimeOffset.Now;
            var timestamp = payload.TimestampUtc.ToDateTimeOffset();
            if (timestamp > nowTimestamp.AddSeconds(1) || timestamp < nowTimestamp.AddSeconds(-30))
            {
                throw new InvalidOperationException("Request timestamp is too far in the future or the past.");
            }
            if (payload.ExpiresUtc.ToDateTimeOffset() < timestamp)
            {
                throw new InvalidOperationException("Request expiration is before the timestamp.");
            }

            if (!payload.SignedPreKey.HasPublicKey || !payload.SignedPreKey.HasSignature)
            {
                throw new InvalidOperationException("Signed pre-key missing public key or signature.");
            }

            var signedPreKeyBytes = payload.SignedPreKey.PublicKey.ToByteArray();
            var signedPreKeySignature = new CryptoSignature(payload.SignedPreKey.Signature.ToByteArray());
            if (!_signingService.Verify(signedPreKeyBytes, signedPreKeySignature, new PublicKey(request.IdentityKeyBytes)))
            {
                throw new InvalidOperationException("Invalid signed pre-key signature.");
            }

            // Resolve peer from signing key via connection info
            var remoteIdentitySigningKeyBytes = request.IdentityKeyBytes;
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

            var signedPreKeyId = new Guid(payload.SignedPreKey.Id.ToByteArray());
            var domainBundles = new List<Percolator.Cryptography.PreKeyBundle>();
            foreach (var oneTimePreKey in payload.OneTimePreKeys)
            {
                if (oneTimePreKey is null || !oneTimePreKey.HasId || !oneTimePreKey.HasPublicKey)
                {
                    throw new InvalidOperationException("Invalid one-time pre-key entry.");
                }
                var oneTimePreKeyId = new Guid(oneTimePreKey.Id.ToByteArray());

                var domainBundle = new Percolator.Cryptography.PreKeyBundle(
                    new RatchetIdentityKey(request.IdentityKeyBytes),
                    signedPreKeyId,
                    new PreKey(signedPreKeyBytes),
                    signedPreKeySignature,
                    oneTimePreKeyId,
                    new OneTimeKey(oneTimePreKey.PublicKey.ToByteArray()),
                    payload.ExpiresUtc.ToDateTime()
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
