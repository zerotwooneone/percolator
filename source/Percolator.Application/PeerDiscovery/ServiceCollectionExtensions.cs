using Microsoft.Extensions.DependencyInjection;

namespace Percolator.Application.PeerDiscovery;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddPeerDiscovery(this IServiceCollection services)
    {
        services.AddSingleton<IPeerConnectionManager, PeerConnectionManager>();

        return services;
    }
}
