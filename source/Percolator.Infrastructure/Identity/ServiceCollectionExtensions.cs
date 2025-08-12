using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Percolator.Identity;
using Percolator.Infrastructure.Persistence;

namespace Percolator.Infrastructure.Identity;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddIdentityInfrastructure(this IServiceCollection services)
    {
        services.AddDbContext<PercolatorDbContext>((sp, options) =>
        {
            var storageOptions = sp.GetRequiredService<IOptions<StorageOptions>>().Value;
            var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var percolatorAppDataPath = Path.Combine(appDataPath, storageOptions.Path);
            Directory.CreateDirectory(percolatorAppDataPath);
            var dbPath = Path.Combine(percolatorAppDataPath, "percolator.db");

            options.UseSqlite($"Data Source={dbPath}");
        });
        
        services.AddScoped<IPeerRepository, SqlitePeerRepository>();
        services.AddSingleton<IIdentityStore, FileSystemIdentityStore>();
        services.AddSingleton<IKeyManagementService, PersistentKeyManagementService>();

        return services;
    }
}
