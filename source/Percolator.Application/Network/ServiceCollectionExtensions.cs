using Microsoft.Extensions.DependencyInjection;
using Percolator.Network;

namespace Percolator.Application.Network;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddNetworkServices(this IServiceCollection services)
    {
        services.AddSingleton<IPeerDiscoveryService,PeerDiscoveryService>();
        return services;
    }
}
