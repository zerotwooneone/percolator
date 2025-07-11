using Microsoft.Extensions.DependencyInjection;
using Percolator.Network;

namespace Percolator.Infrastructure.Network;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddNetworkInfrastructure(this IServiceCollection services)
    {
        services.AddSingleton<FileBasedPeerConnectionRepository>();
        services.AddSingleton<IPeerConnectionRepository>(sp => sp.GetRequiredService<FileBasedPeerConnectionRepository>());
        return services;
    }
}
