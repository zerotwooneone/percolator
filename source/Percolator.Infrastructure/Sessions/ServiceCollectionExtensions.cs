using Microsoft.Extensions.DependencyInjection;
using Percolator.Cryptography;
using Percolator.Infrastructure.Cryptography;

namespace Percolator.Infrastructure.Sessions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddSessionsInfrastructure(this IServiceCollection services)
    {
        services.AddScoped<IDoubleRatchetSessionStore, SqliteDoubleRatchetSessionStore>();
        services.AddScoped<Percolator.Application.Network.IRatchetKeySessionLookup, RatchetKeySessionLookup>();
        return services;
    }
}
