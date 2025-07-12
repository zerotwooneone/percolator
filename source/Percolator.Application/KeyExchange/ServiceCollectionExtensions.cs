using Microsoft.Extensions.DependencyInjection;

namespace Percolator.Application.KeyExchange;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddKeyExchangeServices(this IServiceCollection services)
    {
        services.AddSingleton<IX3DHOrchestrator, X3DHOrchestrator>();
        return services;
    }
}
