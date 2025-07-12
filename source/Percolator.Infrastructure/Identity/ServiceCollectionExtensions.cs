using Microsoft.Extensions.DependencyInjection;
using Percolator.Application.Identity;
using Percolator.Identity;
using Percolator.Network;

namespace Percolator.Infrastructure.Identity;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddIdentityInfrastructure(this IServiceCollection services)
    {
        services.AddSingleton<FileBasedPeerRepository>();
        services.AddSingleton<IPeerRepository>(sp => sp.GetRequiredService<FileBasedPeerRepository>());
        services.AddSingleton<ITrustedPeerStore>(sp => sp.GetRequiredService<FileBasedPeerRepository>());
        services.AddSingleton<ICredentialService, CredentialService>();
        services.AddSingleton<ITlsCertificateService, TlsCertificateService>();
        services.AddSingleton<IIdentityStore, FileSystemIdentityStore>();

        return services;
    }
}
