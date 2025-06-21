using MediatR;
using Percolator.Application.Notifications;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Percolator.Application.Handlers
{
    public class AnnounceManifestsOnPeerDiscoveredHandler : INotificationHandler<PeerDiscoveredNotification>
    {
        private readonly PeerConnectionManager _connectionManager;
        private readonly ManifestStore _manifestStore;

        public AnnounceManifestsOnPeerDiscoveredHandler(PeerConnectionManager connectionManager, ManifestStore manifestStore)
        {
            _connectionManager = connectionManager;
            _manifestStore = manifestStore;
        }

        public async Task Handle(PeerDiscoveredNotification notification, CancellationToken cancellationToken)
        {
            var peer = notification.Peer;
            Console.WriteLine($"+ Peer discovered: {peer.IpAddress}:{peer.GrpcEndpoint.Port}");

            // Announce all our available manifests to the new peer
            foreach (var manifestHash in _manifestStore.GetManifestHashes())
            {
                try
                {
                    var signedManifest = _manifestStore.GetManifest(manifestHash);
                    if (signedManifest is null)
                    {
                        Console.WriteLine($"[Announcer] Could not find manifest for hash {manifestHash.ToBase64()} to announce. Skipping.");
                        continue;
                    }

                    var client = _connectionManager.GetClient(peer);
                    var request = new Contracts.Protos.AnnounceManifestRequest
                    {
                        ManifestHash = manifestHash,
                        SignedManifest = signedManifest
                    };
                    await client.AnnounceManifestAsync(request, cancellationToken: cancellationToken);
                    Console.WriteLine($"Announced existing manifest to new peer {peer.IpAddress}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error announcing manifest to peer {peer.IpAddress}: {ex.Message}");
                }
            }
        }
    }
}
