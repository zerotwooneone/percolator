using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Percolator.Application.Identity;
using Percolator.Identity;
using Percolator.Infrastructure.Persistence;
using Percolator.Infrastructure.Security;
using SQLitePCL;

namespace Percolator.Infrastructure.Identity
{
    public static class ServiceCollectionExtensions
    {
        public static IServiceCollection AddIdentityInfrastructure(this IServiceCollection services)
        {
            // Initialize the SQLCipher provider
            Batteries.Init();

            services.AddSingleton<IDatabaseEncryptionService, DatabaseEncryptionService>();
            
            // Store for X3DH keys bound to SelfIdentityId
            services.AddScoped<ISelfIdentityKeysStore, SqliteSelfIdentityKeysStore>();
            services.AddScoped<IPeerRepository, SqlitePeerRepository>();
            services.AddScoped<ISelfIdentityRepository, SqliteSelfIdentityRepository>();
            services.AddScoped<IPeerPublicSigningKeyStore, SqlitePeerPublicSigningKeyStore>();

            services.AddDbContext<PercolatorDbContext>((provider, options) =>
            {
                var storageOptions = provider.GetRequiredService<IOptions<StorageOptions>>().Value;
                var encryptionService = provider.GetRequiredService<IDatabaseEncryptionService>();
                var configuration = provider.GetRequiredService<IConfiguration>();

                // Ensure the data directory exists
                Directory.CreateDirectory(storageOptions.Path);

                var password = encryptionService.GetDatabasePassword();
                var configuredFileName = configuration["Percolator:DatabaseFileName"];
                // Default to percolator.db if not set; ensure only a file name is used
                var dbFileName = string.IsNullOrWhiteSpace(configuredFileName) ? "percolator.db" : Path.GetFileName(configuredFileName);
                var dbPath = Path.Combine(storageOptions.Path, dbFileName);

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
