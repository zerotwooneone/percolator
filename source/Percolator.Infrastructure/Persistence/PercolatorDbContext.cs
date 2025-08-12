using Microsoft.EntityFrameworkCore;
using Percolator.Identity;

namespace Percolator.Infrastructure.Persistence;

public class PercolatorDbContext : DbContext
{
    public PercolatorDbContext(DbContextOptions<PercolatorDbContext> options) : base(options)
    {
    }

    public DbSet<Peer> Peers { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Peer>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id)
                .HasConversion(peerId => peerId.Value, value => new PeerId(value))
                .ValueGeneratedNever();

            entity.Property(e => e.Name).IsRequired();
        });
    }
}
