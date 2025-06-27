using Microsoft.Extensions.Hosting;
using Percolator.Network;

namespace Percolator.Application.Network
{
    /// <summary>
    /// A hosted service that manages the lifecycle of the <see cref="IPeerDiscoveryService"/>.
    /// It ensures that peer discovery is only started after the application is fully running,
    /// preventing race conditions with the gRPC server.
    /// </summary>
    public class PeerDiscoveryHostedService : IHostedService
    {
        private readonly IPeerDiscoveryService _peerDiscoveryService;
        private readonly IHostApplicationLifetime _hostApplicationLifetime;

        public PeerDiscoveryHostedService(IPeerDiscoveryService peerDiscoveryService, IHostApplicationLifetime hostApplicationLifetime)
        {
            _peerDiscoveryService = peerDiscoveryService;
            _hostApplicationLifetime = hostApplicationLifetime;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _hostApplicationLifetime.ApplicationStarted.Register(() =>
            {
                // Don't block the startup process, run discovery in the background
                _ = _peerDiscoveryService.StartAsync(_hostApplicationLifetime.ApplicationStopping);
            });

            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            _peerDiscoveryService.Stop();
            return Task.CompletedTask;
        }
    }
}
