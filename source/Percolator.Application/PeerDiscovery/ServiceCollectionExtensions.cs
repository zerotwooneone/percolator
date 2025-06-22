using Microsoft.Extensions.DependencyInjection;
using Percolator.Network;

namespace Percolator.Application.PeerDiscovery;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddPeerDiscovery(this IServiceCollection services)
    {
        services.AddSingleton<IPeerConnectionManager, PeerConnectionManager>();
        services.AddSingleton<IDiscoverySignatureProvider, DiscoverySignatureProvider>();

        return services;
    }
}
