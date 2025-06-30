using Microsoft.Extensions.DependencyInjection;

namespace Percolator.Application.KeyExchange;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddKeyExchangeServices(this IServiceCollection services)
    {
        services.AddSingleton<X3DHOrchestrator>();
        return services;
    }
}
