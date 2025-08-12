using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;
using SQLitePCL;
using System.IO;
using Microsoft.Data.Sqlite;

namespace Percolator.Infrastructure.Persistence
{
    public class PercolatorDbContextFactory : IDesignTimeDbContextFactory<PercolatorDbContext>
    {
        public PercolatorDbContext CreateDbContext(string[] args)
        {
            // Required for the design-time tools to find the SQLCipher binaries
            Batteries.Init();

            var optionsBuilder = new DbContextOptionsBuilder<PercolatorDbContext>();
            var dbPath = Path.Combine(Directory.GetCurrentDirectory(), "percolator-design-time.db");

            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Password = "DESIGN_TIME_PASSWORD"
            }.ToString();

            optionsBuilder.UseSqlite(connectionString);

            return new PercolatorDbContext(optionsBuilder.Options);
        }
    }
}
