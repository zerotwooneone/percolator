using Microsoft.Extensions.DependencyInjection;
using Percolator.Identity;
using CertificateOperations = Percolator.Application.Identity.CertificateOperations;

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

        // Adapter implementation from Percolator.Application
        services.AddSingleton<ICertificateOperations, CertificateOperations>();
        
        // Still needs a concrete implementation
        services.AddSingleton<IPeerRepository, InMemoryPeerRepository>();

        services.AddSingleton<ActiveIdentityContext>();
        return services;
    }
}
