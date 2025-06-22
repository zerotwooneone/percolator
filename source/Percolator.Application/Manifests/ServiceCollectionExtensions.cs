using Microsoft.Extensions.DependencyInjection;

namespace Percolator.Application.Manifests;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddManifests(this IServiceCollection services)
    {
        services.AddSingleton<IManifestService, ManifestService>();
        services.AddSingleton<IManifestStore, ManifestStore>();
        services.AddSingleton<ISharedDirectoryProvider, SharedDirectoryProvider>();

        return services;
    }
}
