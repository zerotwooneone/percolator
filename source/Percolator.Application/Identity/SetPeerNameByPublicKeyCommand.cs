using System;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using MediatR;
using Microsoft.Extensions.Logging;
using Percolator.Identity;

namespace Percolator.Application.Identity
{
    public sealed record SetPeerNameByPublicKeyCommand(string Name, byte[] IdentitySigningKeySpki) : IRequest;

    public sealed class SetPeerNameByPublicKeyHandler : IRequestHandler<SetPeerNameByPublicKeyCommand>
    {
        private readonly ILogger<SetPeerNameByPublicKeyHandler> _logger;
        private readonly IPeerPublicSigningKeyStore _keyStore;
        private readonly IPeerRepository _peerRepository;

        public SetPeerNameByPublicKeyHandler(
            ILogger<SetPeerNameByPublicKeyHandler> logger,
            IPeerPublicSigningKeyStore keyStore,
            IPeerRepository peerRepository)
        {
            _logger = logger;
            _keyStore = keyStore;
            _peerRepository = peerRepository;
        }

        public async Task Handle(SetPeerNameByPublicKeyCommand request, CancellationToken cancellationToken)
        {
            if (request.IdentitySigningKeySpki is null || request.IdentitySigningKeySpki.Length == 0)
                throw new ArgumentException("Identity signing key SPKI is required.");
            if (string.IsNullOrWhiteSpace(request.Name))
                throw new ArgumentException("Peer name is required.");

            var pkh = SHA256.HashData(request.IdentitySigningKeySpki);

            // Try to resolve an existing peer by PKH mapping
            var existingPeerId = await _keyStore.GetPeerIdByPublicKeyHashAsync(pkh, cancellationToken).ConfigureAwait(false);
            Peer? peer = null;
            if (existingPeerId is not null)
            {
                peer = await _peerRepository.GetByIdAsync(existingPeerId).ConfigureAwait(false);
                if (peer is null)
                {
                    // Create the peer with the known ID
                    peer = new Peer(existingPeerId, request.Name);
                    await _peerRepository.AddAsync(peer).ConfigureAwait(false);
                }
                else if (!string.Equals(peer.Name, request.Name, StringComparison.Ordinal))
                {
                    // Repository has no update; replace entry with same ID and new name
                    await _peerRepository.RemoveAsync(existingPeerId).ConfigureAwait(false);
                    await _peerRepository.AddAsync(new Peer(existingPeerId, request.Name)).ConfigureAwait(false);
                }
            }
            else
            {
                // No mapping yet; create or reuse by name
                peer = await _peerRepository.GetByNameAsync(request.Name).ConfigureAwait(false);
                if (peer is null)
                {
                    var newId = PeerId.NewId();
                    peer = new Peer(newId, request.Name);
                    await _peerRepository.AddAsync(peer).ConfigureAwait(false);
                }
            }

            // Ensure PKH mapping is active for this peer
            await _keyStore.ActivateIfChangedAsync(peer!.Id, request.IdentitySigningKeySpki, pkh, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Set peer '{Name}' with id {PeerId} by public key (pkh={Pkh})", request.Name, peer.Id.Value, Convert.ToHexString(pkh));
        }
    }
}
