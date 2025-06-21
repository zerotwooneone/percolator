using MediatR;
using Microsoft.Extensions.Logging;
using Percolator.Application.Notifications;

namespace Percolator.Application.Handlers
{
    public class AnnounceManifestsOnPeerDiscoveredHandler : INotificationHandler<PeerDiscoveredNotification>
    {
        private readonly ILogger<AnnounceManifestsOnPeerDiscoveredHandler> _logger;
        private readonly PeerConnectionManager _connectionManager;
        private readonly ManifestStore _manifestStore;

        public AnnounceManifestsOnPeerDiscoveredHandler(ILogger<AnnounceManifestsOnPeerDiscoveredHandler> logger, PeerConnectionManager connectionManager, ManifestStore manifestStore)
        {
            _logger = logger;
            _connectionManager = connectionManager;
            _manifestStore = manifestStore;
        }

        public async Task Handle(PeerDiscoveredNotification notification, CancellationToken cancellationToken)
        {
            var peer = notification.Peer;
            _logger.LogInformation("+ Peer discovered: {IpAddress}:{Port}", peer.IpAddress, peer.GrpcEndpoint.Port);

            // Announce all our available manifests to the new peer
            foreach (var manifestHash in _manifestStore.GetManifestHashes())
            {
                try
                {
                    var signedManifest = _manifestStore.GetManifest(manifestHash);
                    if (signedManifest is null)
                    {
                        _logger.LogWarning("Could not find manifest for hash {ManifestHash} to announce. Skipping.", manifestHash.ToBase64());
                        continue;
                    }

                    var client = _connectionManager.GetClient(peer);
                    var request = new Contracts.Protos.AnnounceManifestRequest
                    {
                        ManifestHash = manifestHash,
                        SignedManifest = signedManifest
                    };
                    await client.AnnounceManifestAsync(request, cancellationToken: cancellationToken);
                    _logger.LogInformation("Announced existing manifest to new peer {IpAddress}", peer.IpAddress);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error announcing manifest to peer {IpAddress}", peer.IpAddress);
                }
            }
        }
    }
}
