using Microsoft.Extensions.DependencyInjection;
using Percolator.Application.Sessions;
using Percolator.Cryptography;

namespace Percolator.Infrastructure.Sessions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddSessionsInfrastructure(this IServiceCollection services)
    {
        services.AddScoped<IRatchetKeyIndex, RatchetKeyIndexAdapter>();
        services.AddScoped<ISessionCatalog, SessionCatalogAdapter>();
        services.AddScoped<IPeerConnectionQueries, PeerConnectionQueries>();
        return services;
    }
}
