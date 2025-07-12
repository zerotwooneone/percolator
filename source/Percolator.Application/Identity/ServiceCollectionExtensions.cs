using Microsoft.Extensions.DependencyInjection;
using Percolator.Application.Network;
using Percolator.Application.Sessions;
using Percolator.Cryptography;
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
        
        services.AddSingleton<ICredentialService, CredentialService>();
        // Application-layer orchestrator
        services.AddSingleton<IIdentityOrchestrator, IdentityOrchestrator>();
        services.AddSingleton<ITlsCertificateService, TlsCertificateService>();

        services.AddSingleton<IDirectSessionManager, DirectSessionManager>();

        return services;
    }
}
