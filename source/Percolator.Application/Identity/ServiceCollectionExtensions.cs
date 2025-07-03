using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Percolator.Identity;

namespace Percolator.Application.Identity;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddIdentityServices(this IServiceCollection services)
    {
        // Concrete implementations from Percolator.Identity
        services.AddSingleton<IIdentityService, PersistentIdentityService>();
        services.AddSingleton<IIdentityStore, FileSystemIdentityStore>();
        services.AddSingleton<IKeyManagementService, PersistentKeyManagementService>();
        services.AddSingleton<ICredentialService, CredentialService>();

        // Still needs a concrete implementation
        services.AddSingleton<IPeerRepository, InMemoryPeerRepository>();

        // Application-layer orchestrator
        services.AddHostedService<IdentityOrchestrator>();

        services.AddSingleton<ActiveIdentityContext>();
        return services;
    }
}
