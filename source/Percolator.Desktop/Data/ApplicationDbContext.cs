using Microsoft.EntityFrameworkCore;

namespace Percolator.Desktop.Data;

public class ApplicationDbContext : DbContext
{
    public DbSet<RemoteClient> RemoteClients { get; set; }
    public DbSet<RemoteClientIp> RemoteClientIps { get; set; }
    public string DbPath { get; }
    public ApplicationDbContext()
    {
        var path = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        DbPath = System.IO.Path.Join(path,"ZeroHome","Percolator", "app.db");
    }
    
    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
        : base(options)
    {
        var path = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        DbPath = System.IO.Path.Join(path,"ZeroHome","Percolator", "app.db");
    }
    
    protected override void OnConfiguring(DbContextOptionsBuilder options)
        => options.UseSqlite($"Data Source={DbPath}");
}