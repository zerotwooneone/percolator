using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Percolator.Network;

namespace Percolator.Application.Network
{
    public static class ServiceCollectionExtensions
    {
        public static IServiceCollection AddNetworkServices(this IServiceCollection services, IConfiguration config, int? listenPortOverride = null)
        {
            var peerDiscoveryConfig = new PeerDiscoveryConfig();
            config.GetSection("PeerDiscovery").Bind(peerDiscoveryConfig);

            if (listenPortOverride.HasValue)
            {
                peerDiscoveryConfig.ListenPort = listenPortOverride.Value;
            }

            services.AddSingleton<IPeerDiscoveryConfig>(peerDiscoveryConfig);
            services.AddSingleton<IPeerDiscoveryService, PeerDiscoveryService>();
            return services;
        }
    }
}
