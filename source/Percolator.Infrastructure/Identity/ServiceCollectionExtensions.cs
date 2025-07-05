using Microsoft.Extensions.DependencyInjection;
using Percolator.Identity;

namespace Percolator.Infrastructure.Identity;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddIdentityInfrastructure(this IServiceCollection services)
    {
        services.AddSingleton<IPeerRepository, FileBasedPeerRepository>();
        return services;
    }
}
