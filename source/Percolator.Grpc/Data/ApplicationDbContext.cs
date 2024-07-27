using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Percolator.Grpc.Data;

public class ApplicationDbContext : IdentityDbContext
{
    public DbSet<KnownKeys> KnownKeys { get; set; }
    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
        : base(options)
    {
    }
}