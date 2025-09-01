using Microsoft.Extensions.DependencyInjection;

namespace Percolator.Application.PeerDiscovery;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddPeerDiscoveryServices(this IServiceCollection services)
    {
        // Register the handler for discovered peers
        services.AddScoped<IPeerConnectionManager, PeerConnectionManager>();
        return services;
    }
}
