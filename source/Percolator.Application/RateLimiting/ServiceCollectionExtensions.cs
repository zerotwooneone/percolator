using Microsoft.Extensions.DependencyInjection;

namespace Percolator.Application.RateLimiting;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddRateLimiting(this IServiceCollection services)
    {
        services.AddSingleton<IRateLimiter, InMemoryRateLimiter>();

        return services;
    }
}
