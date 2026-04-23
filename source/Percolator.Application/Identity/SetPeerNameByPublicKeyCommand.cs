using System.Security.Cryptography;
using MediatR;
using Microsoft.Extensions.Logging;
using Percolator.Identity;
using Percolator.Identity.Model;

namespace Percolator.Application.Identity
{
    public sealed record SetPeerNameByPublicKeyCommand(string Name, byte[] IdentitySigningKeySpki) : IRequest;

    public sealed class SetPeerNameByPublicKeyHandler : IRequestHandler<SetPeerNameByPublicKeyCommand>
    {
        private readonly ILogger<SetPeerNameByPublicKeyHandler> _logger;
        private readonly IPeerPublicSigningKeyStore _keyStore;
        private readonly IPeerIdentityRepository _peerIdentityRepository;

        public SetPeerNameByPublicKeyHandler(
            ILogger<SetPeerNameByPublicKeyHandler> logger,
            IPeerPublicSigningKeyStore keyStore,
            IPeerIdentityRepository peerIdentityRepository)
        {
            _logger = logger;
            _keyStore = keyStore;
            _peerIdentityRepository = peerIdentityRepository;
        }

        public async Task Handle(SetPeerNameByPublicKeyCommand request, CancellationToken cancellationToken)
        {
            if (request.IdentitySigningKeySpki is null || request.IdentitySigningKeySpki.Length == 0)
                throw new ArgumentException("Identity signing key SPKI is required.");
            if (string.IsNullOrWhiteSpace(request.Name))
                throw new ArgumentException("Peer name is required.");

            var pkh = SHA256.HashData(request.IdentitySigningKeySpki);
            var identityPublicKeyHash = IdentityPublicKeyHash.FromBytes(pkh);

            // Try to resolve an existing peer by PKH mapping
            var existingPeerId = await _keyStore.GetPeerIdByPublicKeyHashAsync(identityPublicKeyHash, cancellationToken).ConfigureAwait(false);
            PeerIdentity identity;
            if (existingPeerId is not null)
            {
                identity = await _peerIdentityRepository.GetByIdAsync(existingPeerId).ConfigureAwait(false)
                    ?? new PeerIdentity(existingPeerId);
                identity.SetDisplayName(new DisplayName(request.Name));
                await _peerIdentityRepository.SaveAsync(identity).ConfigureAwait(false);
            }
            else
            {
                // No mapping yet; create or reuse by name
                identity = await _peerIdentityRepository.GetByNameAsync(new DisplayName(request.Name)).ConfigureAwait(false)
                    ?? new PeerIdentity(PeerId.NewId());
                identity.SetDisplayName(new DisplayName(request.Name));
                await _peerIdentityRepository.SaveAsync(identity).ConfigureAwait(false);
            }

            // Ensure PKH mapping is active for this peer
            await _keyStore.ActivateIfChangedAsync(identity.Id, request.IdentitySigningKeySpki, identityPublicKeyHash, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Set peer '{Name}' with id {PeerId} by public key (pkh={Pkh})", request.Name, identity.Id.Value, Convert.ToHexString(pkh));
        }
    }
}
