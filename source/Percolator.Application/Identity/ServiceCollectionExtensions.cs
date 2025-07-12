using Microsoft.Extensions.DependencyInjection;
using Percolator.Identity;

namespace Percolator.Application.Identity;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddIdentityServices(this IServiceCollection services)
    {
        // Concrete implementations from Percolator.Identity
        services.AddSingleton<IIdentityService, PersistentIdentityService>();
        
        services.AddSingleton<IKeyManagementService, PersistentKeyManagementService>();
        services.AddSingleton<ICredentialService, CredentialService>();

        // Peer repository is now handled by the infrastructure layer
        // services.AddSingleton<IPeerRepository, InMemoryPeerRepository>();

        // Application-layer orchestrator
        services.AddSingleton<IIdentityOrchestrator, IdentityOrchestrator>();
        services.AddSingleton<ITlsCertificateService, TlsCertificateService>();

        services.AddSingleton<ActiveIdentityContext>();
        return services;
    }
}
