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
    public DbSet<PeerPublicSigningKeyDbo> PeerPublicSigningKeys { get; set; } = null!;
    public DbSet<DhtNode> DhtNodes { get; set; } = null!;
    public DbSet<PeerConnectionDbo> PeerConnections { get; set; } = null!;
    public DbSet<GrpcEndPointDbo> GrpcEndPoints { get; set; } = null!;
    public DbSet<TlsCertificateDbo> TlsCertificates { get; set; } = null!;
    public DbSet<DirectSessionDbo> DirectSessions { get; set; } = null!;
    public DbSet<DoubleRatchetSessionDbo> DoubleRatchetSessions { get; set; } = null!;
    public DbSet<SkippedMessageKeyDbo> SkippedMessageKeys { get; set; } = null!;
    public DbSet<ConversationDbo> Conversations { get; set; } = null!;
    public DbSet<MessageDbo> Messages { get; set; } = null!;
    public DbSet<ConversationParticipantDbo> ConversationParticipants { get; set; } = null!;
    public DbSet<MessageQueueItemDbo> MessageQueueItems { get; set; } = null!;
    public DbSet<SelfIdentityDbo> SelfIdentities { get; set; } = null!;
    public DbSet<SelfIdentityKnownPeerDbo> SelfIdentityKnownPeers { get; set; } = null!;
    public DbSet<SelfIdentityKeysDbo> SelfIdentityKeys { get; set; } = null!;

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

        // SelfIdentity
        modelBuilder.Entity<SelfIdentityDbo>(entity =>
        {
            entity.ToTable("SelfIdentity");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedOnAdd();
            entity.Property(e => e.PeerId).IsRequired();
            entity.Property(e => e.Name).IsRequired();
            entity.HasIndex(e => e.Name).IsUnique();
            entity.HasIndex(e => e.PeerId); // non-unique
        });

        // SelfIdentityKeys (one-to-one with SelfIdentity)
        modelBuilder.Entity<SelfIdentityKeysDbo>(entity =>
        {
            entity.ToTable("SelfIdentityKeys");
            entity.HasKey(e => e.SelfIdentityId);
            entity.Property(e => e.SelfIdentityId).ValueGeneratedNever();
            entity.Property(e => e.IdentitySigningKey).IsRequired();
            entity.Property(e => e.SignedPreKey).IsRequired();

            entity.HasOne<SelfIdentityDbo>()
                .WithOne(i => i.Keys)
                .HasForeignKey<SelfIdentityKeysDbo>(k => k.SelfIdentityId)
                .OnDelete(DeleteBehavior.Cascade)
                .IsRequired();
        });

        // SelfIdentityKnownPeer
        modelBuilder.Entity<SelfIdentityKnownPeerDbo>(entity =>
        {
            entity.ToTable("SelfIdentityKnownPeer");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedOnAdd();
            entity.Property(e => e.SelfIdentityId).IsRequired();
            entity.Property(e => e.PeerId).IsRequired();
            entity.HasIndex(e => new { e.SelfIdentityId, e.PeerId }).IsUnique();
            entity.HasOne<SelfIdentityDbo>()
                .WithMany()
                .HasForeignKey(e => e.SelfIdentityId)
                .OnDelete(DeleteBehavior.Cascade)
                .IsRequired();
        });

        // DirectSession
        modelBuilder.Entity<DirectSessionDbo>(entity =>
        {
            entity.ToTable("DirectSession");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id)
                .ValueGeneratedOnAdd();
            entity.Property(e => e.RemotePeerId)
                .HasConversion(v => v.Value, v => new PeerId(v))
                .IsRequired();
            entity.Property(e => e.SessionId)
                .IsRequired();
            entity.Property(e => e.SelfIdentityId)
                .IsRequired();
            // Non-unique helper indexes for common lookups
            entity.HasIndex(e => new { e.SelfIdentityId, e.RemotePeerId });
            entity.HasIndex(e => new { e.SelfIdentityId, e.SessionId });
            entity.HasOne<PeerConnectionDbo>()
                .WithMany()
                .HasForeignKey(e => e.RemotePeerId)
                .OnDelete(DeleteBehavior.Cascade)
                .IsRequired();
            entity.HasOne<SelfIdentityDbo>()
                .WithMany()
                .HasForeignKey(e => e.SelfIdentityId)
                .OnDelete(DeleteBehavior.Cascade)
                .IsRequired();
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

        modelBuilder.Entity<PeerPublicSigningKeyDbo>(entity =>
        {
            entity.ToTable("PeerPublicSigningKeys");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.PeerId)
                .HasConversion(v => v.Value, v => new PeerId(v))
                .IsRequired();
            entity.Property(e => e.PublicKey).IsRequired();
            entity.Property(e => e.PublicKeyHash).IsRequired();
            entity.Property(e => e.ActiveAtUtc).IsRequired();
            // ExpiredAtUtc nullable

            entity.HasIndex(e => e.PeerId);
            entity.HasIndex(e => e.PublicKeyHash).IsUnique();
            entity.HasIndex(e => new { e.PeerId, e.ActiveAtUtc });
            entity.HasIndex(e => new { e.PeerId, e.ExpiredAtUtc });
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

        // DoubleRatchetSession
        modelBuilder.Entity<DoubleRatchetSessionDbo>(entity =>
        {
            entity.ToTable("DoubleRatchetSessions");
            entity.HasKey(e => e.SessionId);
            entity.Property(e => e.SessionId)
                .ValueGeneratedNever();

            entity.Property(e => e.SelfIdentityId).IsRequired();
            entity.HasAlternateKey(e => new { e.SessionId, e.SelfIdentityId });
            entity.Property(e => e.RootKey).IsRequired();
            entity.Property(e => e.RatchetFlag).IsRequired();
            entity.Property(e => e.SendingChainKey);
            entity.Property(e => e.ReceivingChainKey);
            entity.Property(e => e.SendingCounter).IsRequired();
            entity.Property(e => e.ReceivingCounter).IsRequired();
            entity.Property(e => e.PreviousChainLength).IsRequired();
            entity.Property(e => e.TheirDhRatchetPublicKey);
            entity.Property(e => e.DhRatchetPrivateKey);
            entity.Property(e => e.TheirIdentityPublicKey).IsRequired();
            entity.Property(e => e.UpdatedAt).IsRequired();

            entity.HasMany(e => e.SkippedMessageKeys)
                .WithOne(k => k.Session)
                .HasForeignKey(k => new { k.SessionId, k.SelfIdentityId })
                .HasPrincipalKey(e => new { e.SessionId, e.SelfIdentityId })
                .IsRequired()
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(e => new { e.SelfIdentityId, e.UpdatedAt });
            entity.HasOne<SelfIdentityDbo>()
                .WithMany()
                .HasForeignKey(e => e.SelfIdentityId)
                .OnDelete(DeleteBehavior.Cascade)
                .IsRequired();
        });

        // SkippedMessageKey (surrogate PK with uniqueness constraint)
        modelBuilder.Entity<SkippedMessageKeyDbo>(entity =>
        {
            entity.ToTable("SkippedMessageKeys");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.RatchetKey).IsRequired();
            entity.Property(e => e.MessageNumber).IsRequired();
            entity.Property(e => e.MessageKey).IsRequired();
            entity.Property(e => e.SelfIdentityId).IsRequired();
            entity.HasIndex(e => new { e.SelfIdentityId, e.SessionId, e.RatchetKey, e.MessageNumber }).IsUnique();
        });

        // Conversations
        modelBuilder.Entity<ConversationDbo>(entity =>
        {
            entity.ToTable("Conversations");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedNever();
            entity.Property(e => e.ChannelId).IsRequired();
            entity.Property(e => e.SelfIdentityId).IsRequired();
            entity.Property(e => e.CreatedAt).IsRequired();
            entity.Property(e => e.UpdatedAt).IsRequired();
            // Non-unique composite index to speed lookups per identity
            entity.HasIndex(e => new { e.SelfIdentityId, e.ChannelId });
            entity.HasOne<SelfIdentityDbo>()
                .WithMany()
                .HasForeignKey(e => e.SelfIdentityId)
                .OnDelete(DeleteBehavior.Cascade)
                .IsRequired();

            entity.HasMany(e => e.Messages)
                .WithOne(m => m.Conversation)
                .HasForeignKey(m => m.ConversationId)
                .IsRequired()
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(e => e.Participants)
                .WithOne(p => p.Conversation)
                .HasForeignKey(p => p.ConversationId)
                .IsRequired()
                .OnDelete(DeleteBehavior.Cascade);
        });

        // Messages
        modelBuilder.Entity<MessageDbo>(entity =>
        {
            entity.ToTable("Messages");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedNever();
            entity.Property(e => e.ConversationId).IsRequired();
            entity.Property(e => e.SenderId).IsRequired();
            entity.Property(e => e.Body).IsRequired();
            entity.Property(e => e.SentAt).IsRequired();
            entity.HasIndex(e => new { e.ConversationId, e.SentAt });
        });

        // ConversationParticipants (composite key)
        modelBuilder.Entity<ConversationParticipantDbo>(entity =>
        {
            entity.ToTable("ConversationParticipants");
            entity.HasKey(e => new { e.ConversationId, e.ParticipantId });
            entity.Property(e => e.ConversationId).IsRequired();
            entity.Property(e => e.ParticipantId).IsRequired();
        });

        // MessageQueue
        modelBuilder.Entity<MessageQueueItemDbo>(entity =>
        {
            entity.ToTable("MessageQueue");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedOnAdd();
            entity.Property(e => e.RecipientPeerId)
                .HasConversion(v => v.Value, v => new PeerId(v))
                .IsRequired();
            entity.Property(e => e.Blob).IsRequired();
            entity.Property(e => e.EnqueuedAtUtc).IsRequired();
            entity.HasIndex(e => e.EnqueuedAtUtc);
            entity.HasIndex(e => e.RecipientPeerId);
            entity.HasIndex(e => new { e.RecipientPeerId, e.EnqueuedAtUtc });
        });
    }
}
