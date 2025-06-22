using Microsoft.Extensions.DependencyInjection;


namespace Percolator.Application.Security;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddAppSecurity(this IServiceCollection services)
    {
        services.AddSingleton<ITrustedPeerStore, InMemoryTrustedPeerStore>();

        return services;
    }
}
