using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Percolator.Application.PeerDiscovery;
using Percolator.Network;

namespace Percolator.Application.Network;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddNetworkServices(this IServiceCollection services, IConfiguration configuration)
    {
        // Bind the config from appsettings.json
        var config = new PeerDiscoveryConfig();
        configuration.GetSection("PeerDiscovery").Bind(config);
        services.AddSingleton<IPeerDiscoveryConfig>(config);

        // Register concrete implementations from the Application layer
        services.AddSingleton<IIdentityProvider, IdentityProvider>();
        services.AddSingleton<IDiscoverySignatureProvider, DiscoverySignatureProvider>();

        // Register the core service from the Network domain library
        services.AddSingleton<IPeerDiscoveryService, PeerDiscoveryService>();

        // Register the hosted service that runs the discovery
        services.AddHostedService<PeerDiscoveryHostedService>();

        return services;
    }
}
