using Microsoft.EntityFrameworkCore;
using Percolator.Identity;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Percolator.Dht;
using Percolator.Infrastructure.Persistence;

namespace Percolator.Infrastructure.Persistence;

public class PercolatorDbContext : DbContext
{
    public PercolatorDbContext(DbContextOptions<PercolatorDbContext> options) : base(options)
    {
    }

    public DbSet<Peer> Peers { get; set; } = null!;
    public DbSet<PeerIdentityKeyDbo> PeerIdentityKeys { get; set; } = null!;
    public DbSet<SignedPreKeyDbo> SignedPreKeys { get; set; } = null!;
    public DbSet<OneTimePreKeyDbo> OneTimePreKeys { get; set; } = null!;
    public DbSet<DhtNode> DhtNodes { get; set; } = null!;
    public DbSet<PeerConnectionDbo> PeerConnections { get; set; } = null!;
    public DbSet<GrpcEndPointDbo> GrpcEndPoints { get; set; } = null!;
    public DbSet<TlsCertificateDbo> TlsCertificates { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Peer>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id)
                .HasConversion(peerId => peerId.Value, value => new PeerId(value))
                .ValueGeneratedNever();

            entity.Property(e => e.Name).IsRequired();
            entity.HasIndex(e => e.Name).IsUnique();
        });

        modelBuilder.Entity<PeerIdentityKeyDbo>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.PublicKey).IsRequired();
            entity.Property(e => e.PeerId)
                .HasConversion(peerId => peerId.Value, value => new PeerId(value));

            entity.HasOne(d => d.Peer)
                .WithMany()
                .HasForeignKey(d => d.PeerId)
                .IsRequired();
        });

        modelBuilder.Entity<SignedPreKeyDbo>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.PublicKey).IsRequired();
            entity.Property(e => e.Signature).IsRequired();

            entity.HasOne(d => d.PeerIdentityKey)
                .WithMany(p => p.SignedPreKeys)
                .HasForeignKey(d => d.PeerIdentityKeyId)
                .IsRequired();
        });

        modelBuilder.Entity<OneTimePreKeyDbo>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.PublicKey).IsRequired();

            entity.HasOne(d => d.PeerIdentityKey)
                .WithMany(p => p.OneTimePreKeys)
                .HasForeignKey(d => d.PeerIdentityKeyId)
                .IsRequired();
        });

        modelBuilder.Entity<DhtNode>(builder =>
        {
            builder.HasKey(e => e.Id);
            builder.Property(e => e.Id)
                .HasConversion(new ValueConverter<NodeId, byte[]>(v => v.Value, v => new NodeId(v)));

            builder.Property(e => e.EndPoint)
                .HasConversion(new DnsEndPointValueConverter());
        });

        // PeerConnection and children
        modelBuilder.Entity<PeerConnectionDbo>(entity =>
        {
            entity.HasKey(e => e.PeerId);
            entity.Property(e => e.PeerId)
                .HasConversion(v => v.Value, v => new PeerId(v))
                .ValueGeneratedNever();

            entity.Property(e => e.DirectMessagePublicKey);
            entity.HasIndex(e => e.DirectMessagePublicKey);

            entity.HasOne<Peer>()
                .WithOne()
                .HasForeignKey<PeerConnectionDbo>(e => e.PeerId)
                .OnDelete(DeleteBehavior.Cascade)
                .IsRequired();

            entity.HasMany(e => e.GrpcEndPoints)
                .WithOne(x => x.PeerConnection)
                .HasForeignKey(x => x.PeerId)
                .IsRequired()
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(e => e.TlsCertificates)
                .WithOne(x => x.PeerConnection)
                .HasForeignKey(x => x.PeerId)
                .IsRequired()
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<GrpcEndPointDbo>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.PeerId)
                .HasConversion(v => v.Value, v => new PeerId(v));
            entity.Property(e => e.Host).IsRequired();
            entity.Property(e => e.Port).IsRequired();
            entity.Property(e => e.LastSeen).IsRequired();
        });

        modelBuilder.Entity<TlsCertificateDbo>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.PeerId)
                .HasConversion(v => v.Value, v => new PeerId(v));
            entity.Property(e => e.RawData).IsRequired();
            entity.Property(e => e.RawDataHash).IsRequired();
            entity.HasIndex(e => e.RawDataHash);
        });
    }
}
