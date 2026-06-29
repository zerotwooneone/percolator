using Microsoft.Extensions.DependencyInjection;
using Percolator.Identity;

namespace Percolator.Application.Identity;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddIdentityServices(this IServiceCollection services)
    {
        // Concrete implementations from Percolator.Identity
        services.AddSingleton<ICredentialService, CredentialService>();
        services.AddSingleton<Percolator.Cryptography.IOneTimeKeyProvider, Percolator.Cryptography.InMemoryOneTimeKeyProvider>();
        services.AddSingleton<ActiveIdentityContext>();
        services.AddSingleton<IActiveIdentityMutator>(s => s.GetRequiredService<ActiveIdentityContext>());
        services.AddSingleton<IActiveIdentityAccessor, ActiveIdentityAccessor>();
        
        // Application-layer orchestrator
        services.AddScoped<IIdentityOrchestrator, IdentityOrchestrator>();
        
        return services;
    }
}
