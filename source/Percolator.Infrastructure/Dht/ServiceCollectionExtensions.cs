using Microsoft.Extensions.DependencyInjection;
using Percolator.Dht;

namespace Percolator.Infrastructure.Dht;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddDhtInfrastructure(this IServiceCollection services)
    {
        services.AddScoped<IDhtNodeRepository, SqliteDhtNodeRepository>();
        return services;
    }
}
