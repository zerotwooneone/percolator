using Microsoft.Extensions.DependencyInjection;
using Percolator.Application.Network;
using Percolator.Application.Sessions;
using Percolator.Identity;
using Percolator.Network;

namespace Percolator.Application.Identity;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddIdentityServices(this IServiceCollection services)
    {
        // Concrete implementations from Percolator.Identity
        services.AddSingleton<IOneTimeKeyProvider, InMemoryOneTimeKeyProvider>();
        services.AddSingleton<IIdentityService, PersistentIdentityService>();
        services.AddSingleton<ActiveIdentityContext>();
        services.AddSingleton<ISelfIdentityProvider>(s=> s.GetRequiredService<ActiveIdentityContext>());
        
        services.AddSingleton<ICredentialService, CredentialService>();
        // Application-layer orchestrator
        services.AddSingleton<IIdentityOrchestrator, IdentityOrchestrator>();
        
        // Use SharedCertificateAdapter to bridge the shared certificate implementation to the old interface
        services.AddSingleton<ITlsCertificateService, SharedCertificateAdapter>();

        services.AddSingleton<IDirectSessionManager, DirectSessionManager>();

        return services;
    }
}
