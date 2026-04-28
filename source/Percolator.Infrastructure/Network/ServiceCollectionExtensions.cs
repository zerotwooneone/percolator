using Microsoft.Extensions.DependencyInjection;
using Percolator.Network;

namespace Percolator.Infrastructure.Network;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddNetworkInfrastructure(this IServiceCollection services)
    {
        services.AddSingleton<ITrustedPeerStore, FileBasedTrustedPeerStore>();
        services.AddScoped<IDirectSessionRepository, SqliteDirectSessionRepository>();
        // New Network domain repositories
        services.AddScoped<IPeerRoutingProfileRepository, SqlitePeerRoutingProfileRepository>();
        services.AddScoped<IPeerRouteCandidateRepository, SqlitePeerRouteCandidateRepository>();
        services.AddScoped<IDiscoveredPeerRepository, SqliteDiscoveredPeerRepository>();
        return services;
    }
}
