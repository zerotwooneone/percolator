using Microsoft.Extensions.DependencyInjection;
using Percolator.Network;

namespace Percolator.Infrastructure.Network;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddNetworkInfrastructure(this IServiceCollection services)
    {
        services.AddScoped<IPeerConnectionRepository, SqlitePeerConnectionRepository>();
        services.AddSingleton<ITrustedPeerStore, FileBasedTrustedPeerStore>();
        services.AddScoped<IDirectSessionRepository, SqliteDirectSessionRepository>();
        return services;
    }
}
