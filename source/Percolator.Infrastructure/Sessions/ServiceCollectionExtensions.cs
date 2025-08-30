using Microsoft.Extensions.DependencyInjection;
using Percolator.Cryptography;
using Percolator.Infrastructure.Cryptography;

namespace Percolator.Infrastructure.Sessions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddSessionsInfrastructure(this IServiceCollection services)
    {
        services.AddSingleton<IDoubleRatchetSessionStore, SqliteDoubleRatchetSessionStore>();
        return services;
    }
}
