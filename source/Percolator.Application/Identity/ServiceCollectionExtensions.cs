using Microsoft.Extensions.DependencyInjection;
using Percolator.Application.Network;
using Percolator.Identity;
using Percolator.Network;

namespace Percolator.Application.Identity;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddIdentityServices(this IServiceCollection services)
    {
        // Concrete implementations from Percolator.Identity
        services.AddSingleton<ICredentialService, CredentialService>();
        services.AddSingleton<IOneTimeKeyProvider, InMemoryOneTimeKeyProvider>();
        services.AddSingleton<ActiveIdentityContext>();
        services.AddSingleton<IActiveIdentityMutator>(s => s.GetRequiredService<ActiveIdentityContext>());
        
        // Application-layer orchestrator
        services.AddScoped<IIdentityOrchestrator, IdentityOrchestrator>();
        

        return services;
    }
}
