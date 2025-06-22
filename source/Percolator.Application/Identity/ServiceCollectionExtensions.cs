using Microsoft.Extensions.DependencyInjection;
using Percolator.Identity;

namespace Percolator.Application.Identity;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddIdentityServices(this IServiceCollection services)
    {
        services.AddSingleton<ICertificateOperations, CertificateOperations>();
        services.AddSingleton<IPeerIdentityStore, InMemoryPeerIdentityStore>();
        services.AddSingleton<IIdentityService, PersistentIdentityService>();
        services.AddSingleton<IKeyManagementService, PersistentKeyManagementService>();
        services.AddSingleton<ICredentialService, CredentialService>();

        return services;
    }
}
