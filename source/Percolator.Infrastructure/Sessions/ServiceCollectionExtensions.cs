using Microsoft.Extensions.DependencyInjection;
using Percolator.Sessions;

namespace Percolator.Infrastructure.Sessions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddSessionsInfrastructure(this IServiceCollection services)
    {
        services.AddSingleton<IDoubleRatchetSessionStore, FileBasedDoubleRatchetSessionStore>();
        return services;
    }
}
