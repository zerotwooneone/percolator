using Microsoft.Extensions.DependencyInjection;
using Percolator.Identity;

namespace Percolator.Application.Identity;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddIdentityServices(this IServiceCollection services)
    {
        services.AddSingleton<ICredentialService, CredentialService>();
        services.AddSingleton<IKeyManagementService,PersistentKeyManagementService>();
        services.AddSingleton<IIdentityStore, FileSystemIdentityStore>();
        services.AddSingleton<IIdentityService, PersistentIdentityService>();

        services.AddSingleton<ActiveIdentityContext>();
        services.AddSingleton<ICertificateOperations, CertificateOperations>();
        services.AddSingleton<IPeerIdentityStore, InMemoryPeerIdentityStore>();
        services.AddSingleton<IIdentityOrchestrator, IdentityOrchestrator>();

        return services;
    }
}
