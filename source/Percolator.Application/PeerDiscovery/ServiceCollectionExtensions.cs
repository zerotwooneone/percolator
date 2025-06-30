using Microsoft.Extensions.DependencyInjection;
using Percolator.Network;

namespace Percolator.Application.PeerDiscovery;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddPeerDiscoveryServices(this IServiceCollection services)
    {
        // Register the handler for discovered peers
        services.AddSingleton<IPeerDiscoveryHandler, PeerDiscoveryHandler>();
        return services;
    }
}
