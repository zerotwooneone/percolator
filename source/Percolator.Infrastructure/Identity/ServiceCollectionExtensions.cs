using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Percolator.Identity;
using Percolator.Infrastructure.Persistence;
using Percolator.Infrastructure.Security;
using SQLitePCL;
using System.IO;

namespace Percolator.Infrastructure.Identity
{
    public static class ServiceCollectionExtensions
    {
        public static IServiceCollection AddIdentityInfrastructure(this IServiceCollection services)
        {
            // Initialize the SQLCipher provider
            Batteries.Init();

            services.AddSingleton<IDatabaseEncryptionService, DatabaseEncryptionService>();

            services.AddSingleton<IIdentityStore, FileSystemIdentityStore>();
            services.AddSingleton<IKeyManagementService, PersistentKeyManagementService>();
            services.AddSingleton<IIdentityService, PersistentIdentityService>();
            services.AddSingleton<IPeerRepository, SqlitePeerRepository>();

            services.AddDbContext<PercolatorDbContext>((provider, options) =>
            {
                var storageOptions = provider.GetRequiredService<IOptions<StorageOptions>>().Value;
                var encryptionService = provider.GetRequiredService<IDatabaseEncryptionService>();

                // Ensure the data directory exists
                Directory.CreateDirectory(storageOptions.Path);

                var password = encryptionService.GetDatabasePassword();
                var dbPath = Path.Combine(storageOptions.Path, "percolator.db");

                var connectionString = new SqliteConnectionStringBuilder
                {
                    DataSource = dbPath,
                    Password = password
                }.ToString();

                options.UseSqlite(connectionString);
            });
            return services;
        }
    }
}
