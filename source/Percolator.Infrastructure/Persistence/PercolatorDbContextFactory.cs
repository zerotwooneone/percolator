using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;
using System.IO;

namespace Percolator.Infrastructure.Persistence
{
    public class PercolatorDbContextFactory : IDesignTimeDbContextFactory<PercolatorDbContext>
    {
        public PercolatorDbContext CreateDbContext(string[] args)
        {
            // This is a simplified setup for design-time tools. It doesn't use the full app host.
            // It builds a configuration to get the connection string, similar to how the app would.
            var configuration = new ConfigurationBuilder()
                .SetBasePath(Directory.GetCurrentDirectory())
                .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
                .Build();

            var optionsBuilder = new DbContextOptionsBuilder<PercolatorDbContext>();

            // We'll use a hardcoded path for design-time, as the user-specific path isn't available.
            // The actual application will still use the correct path from IOptions<StorageOptions>.
            var dbPath = Path.Combine(Directory.GetCurrentDirectory(), "percolator-design-time.db");
            optionsBuilder.UseSqlite($"Data Source={dbPath}");

            return new PercolatorDbContext(optionsBuilder.Options);
        }
    }
}
