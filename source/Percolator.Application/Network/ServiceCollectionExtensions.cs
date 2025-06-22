using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Percolator.Network;

namespace Percolator.Application.Network;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddNetworkServices(this IServiceCollection services, IConfiguration configuration, int? listenPortOverride = null)
    {
        var config = new PeerDiscoveryConfig();
        configuration.GetSection("PeerDiscovery").Bind(config);
        if (listenPortOverride.HasValue)
        {
            config.ListenPort = listenPortOverride.Value;
        }
        services.AddSingleton<IPeerDiscoveryConfig>(config);

        services.AddSingleton<IPeerDiscoveryService, PeerDiscoveryService>();
        services.AddSingleton<IIdentityProvider, IdentityProvider>();

        return services;
    }
}
