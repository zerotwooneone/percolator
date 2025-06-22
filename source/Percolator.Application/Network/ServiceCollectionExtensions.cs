using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Percolator.Network;

namespace Percolator.Application.Network
{
    public static class ServiceCollectionExtensions
    {
        public static IServiceCollection AddNetworkServices(this IServiceCollection services, IConfiguration config)
        {
            services.Configure<PeerDiscoveryConfig>(config.GetSection("PeerDiscovery"));
            services.AddSingleton<IPeerDiscoveryConfig>(sp => sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<PeerDiscoveryConfig>>().Value);
            services.AddSingleton<IPeerDiscoveryService, PeerDiscoveryService>();
            return services;
        }
    }
}
