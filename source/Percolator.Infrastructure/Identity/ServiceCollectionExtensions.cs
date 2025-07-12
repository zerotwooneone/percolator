using Microsoft.Extensions.DependencyInjection;
using Percolator.Identity;

namespace Percolator.Infrastructure.Identity;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddIdentityInfrastructure(this IServiceCollection services)
    {
        services.AddSingleton<FileBasedPeerRepository>();
        services.AddSingleton<IPeerRepository>(sp => sp.GetRequiredService<FileBasedPeerRepository>());
        services.AddSingleton<IIdentityStore, FileSystemIdentityStore>();
        services.AddSingleton<IKeyManagementService, PersistentKeyManagementService>();

        return services;
    }
}
