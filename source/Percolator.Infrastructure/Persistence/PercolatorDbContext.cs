using Microsoft.EntityFrameworkCore;
using Percolator.Identity;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Percolator.Dht;
using Percolator.Application.Identity;
using Percolator.Infrastructure.Identity;
using Percolator.Infrastructure.Cryptography;

namespace Percolator.Infrastructure.Persistence;

public class PercolatorDbContext : DbContext
{
    private readonly ActiveIdentityContext? _active;

    public PercolatorDbContext(DbContextOptions<PercolatorDbContext> options) : base(options)
    {
    }

    public PercolatorDbContext(DbContextOptions<PercolatorDbContext> options, ActiveIdentityContext active) : base(options)
    {
        _active = active;
    }

    public DbSet<PeerIdentityDbo> PeerIdentities { get; set; } = null!;
    public DbSet<Identity.PeerIdentityKeyDbo> PeerIdentityKeys { get; set; } = null!;
    public DbSet<PeerVerificationDbo> PeerVerifications { get; set; } = null!;
    public DbSet<PreKeyBundleDbo> PreKeyBundles { get; set; } = null!;
    public DbSet<SignedPreKeyDbo> SignedPreKeys { get; set; } = null!;
    public DbSet<OneTimePreKeyDbo> OneTimePreKeys { get; set; } = null!;
    public DbSet<PeerPublicSigningKeyDbo> PeerPublicSigningKeys { get; set; } = null!;
    public DbSet<DhtNode> DhtNodes { get; set; } = null!;
    
    public DbSet<DirectSessionDbo> DirectSessions { get; set; } = null!;
    public DbSet<RatchetKeyIndexDbo> RatchetKeyIndex { get; set; } = null!;
    public DbSet<ConversationDbo> Conversations { get; set; } = null!;
    public DbSet<MessageDbo> Messages { get; set; } = null!;
    public DbSet<ConversationParticipantDbo> ConversationParticipants { get; set; } = null!;
    public DbSet<DirectSessionConversationDbo> DirectSessionConversations { get; set; } = null!;
    public DbSet<MessageQueueItemDbo> MessageQueueItems { get; set; } = null!;
    public DbSet<SelfIdentityDbo> SelfIdentities { get; set; } = null!;
    public DbSet<SelfIdentityKnownPeerDbo> SelfIdentityKnownPeers { get; set; } = null!;
    public DbSet<SelfIdentityKeysDbo> SelfIdentityKeys { get; set; } = null!;
    public DbSet<SelfPreKeySignedDbo> SelfPreKeySigned { get; set; } = null!;
    public DbSet<SelfOneTimePreKeyDbo> SelfOneTimePreKeys { get; set; } = null!;
    public DbSet<ReadReceiptDbo> ReadReceipts { get; set; } = null!;
    public DbSet<EmojiReactionDbo> EmojiReactions { get; set; } = null!;
    public DbSet<DeliveredReceiptDbo> DeliveredReceipts { get; set; } = null!;
    public DbSet<PreHandshakeSessionDbo> PreHandshakeSessions { get; set; } = null!;
    public DbSet<PendingSessionDbo> PendingSessions { get; set; } = null!;
    public DbSet<SentInvitationDbo> SentInvitations { get; set; } = null!;
    public DbSet<SessionDbo> Sessions { get; set; } = null!;
    // New Network domain persistence (PeerRoutingProfile)
    public DbSet<PeerRoutingProfileDbo> PeerRoutingProfiles { get; set; } = null!;
    public DbSet<GrpcEndPointRoutingDbo> PeerRoutingGrpcEndPoints { get; set; } = null!;
    public DbSet<RelayOutboxDbo> RelayOutbox { get; set; } = null!;
    public DbSet<RelayLinkDbo> PeerRoutingRelays { get; set; } = null!;
    public DbSet<PeerRouteCandidateDbo> PeerRouteCandidates { get; set; } = null!;
    public DbSet<DiscoveredPeerDbo> DiscoveredPeers { get; set; } = null!;
    public DbSet<DiscoveredPeerEndpointDbo> DiscoveredPeerEndpoints { get; set; } = null!;
    public DbSet<GroupCryptoStateDbo> GroupCryptoStates { get; set; } = null!;
    public DbSet<Percolator.Infrastructure.Chat.Persistence.GroupMemberDbo> GroupMembers { get; set; } = null!;
    public DbSet<Percolator.Infrastructure.Chat.Persistence.GroupStateDbo> GroupStates { get; set; } = null!;
    public DbSet<Percolator.Infrastructure.Chat.Persistence.PendingGroupInvitationDbo> PendingGroupInvitations { get; set; } = null!;
    public DbSet<Percolator.Infrastructure.Chat.Persistence.SenderKeyRecordDbo> SenderKeyRecords { get; set; } = null!;
    public DbSet<RelayGroupStateDbo> RelayGroupStates { get; set; } = null!;
    public DbSet<RelayBlindedRosterDbo> RelayBlindedRosters { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Ensure EF does not try to map legacy domain type 'Peer'
        modelBuilder.Ignore<global::Percolator.Identity.Peer>();

        // PeerIdentities (authoritative peer catalog for identity aggregate)
        modelBuilder.Entity<PeerIdentityDbo>(entity =>
        {
            entity.ToTable("PeerIdentities");
            entity.HasKey(e => e.PeerId);
            entity.Property(e => e.PeerId)
                .ValueGeneratedOnAdd()
                .HasConversion(
                    v => v.Value,
                    v => new PeerId(v));
            entity.Property(e => e.PublicIdentityId)
                .IsRequired()
                .HasConversion(
                    v => v.Value,
                    v => new PublicIdentityId(v));
            entity.HasIndex(e => e.PublicIdentityId).IsUnique(); // unique index for wire-level identity lookups
            entity.Property(e => e.Name).IsRequired();
            entity.Property(e => e.Version).IsRequired();
            entity.Property(e => e.CreatedAtUtc).IsRequired();
            entity.Property(e => e.UpdatedAtUtc).IsRequired();
            entity.HasIndex(e => e.Name).IsUnique();
            entity.HasMany(e => e.Keys)
                .WithOne(k => k.Peer)
                .HasForeignKey(k => k.PeerId)
                .IsRequired()
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasMany(e => e.Verifications)
                .WithOne()
                .HasForeignKey(v => v.PeerId)
                .IsRequired()
                .OnDelete(DeleteBehavior.Cascade);
        });

        // Sessions (SecureSession persistence)
        modelBuilder.Entity<SessionDbo>(entity =>
        {
            entity.ToTable("Sessions");
            entity.HasKey(e => new { e.SessionId, e.SelfIdentityId });
            entity.Property(e => e.SessionId).IsRequired();
            entity.Property(e => e.SelfIdentityId).IsRequired();
            entity.Property(e => e.RemotePeerId).IsRequired();
            entity.Property(e => e.ProtocolVersion).IsRequired();
            entity.Property(e => e.RootKey).IsRequired();
            entity.Property(e => e.CreatedAtUtc).IsRequired();
            entity.Property(e => e.LastUsedAtUtc).IsRequired();
            entity.HasIndex(e => new { e.SelfIdentityId, e.RemotePeerId });
            entity.HasOne<SelfIdentityDbo>()
                .WithMany()
                .HasForeignKey(e => e.SelfIdentityId)
                .OnDelete(DeleteBehavior.Cascade)
                .IsRequired();
            entity.HasQueryFilter(e => _active != null && _active.Identity != null && e.SelfIdentityId == _active.Identity.SelfIdentityId.Value);
        });

        modelBuilder.Entity<Identity.PeerIdentityKeyDbo>(entity =>
        {
            entity.ToTable("PeerIdentityKeys");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.PeerId).IsRequired();
            entity.Property(e => e.PublicKeySpki).IsRequired();
            entity.Property(e => e.Fingerprint).IsRequired();
            entity.Property(e => e.NotBeforeUtc).IsRequired();
            entity.Property(e => e.ExpiresAtUtc).IsRequired();
            entity.HasIndex(e => e.PeerId);
            entity.HasIndex(e => e.Fingerprint).IsUnique();
        });

        modelBuilder.Entity<PeerVerificationDbo>(entity =>
        {
            entity.ToTable("PeerVerifications");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.PeerId).IsRequired();
            entity.Property(e => e.Fingerprint).IsRequired();
            entity.Property(e => e.Method).IsRequired();
            entity.Property(e => e.VerifiedAtUtc).IsRequired();
            entity.HasIndex(e => new { e.PeerId, e.Fingerprint });
            entity.HasIndex(e => e.Fingerprint);
        });

        // SelfPreKeySigned
        modelBuilder.Entity<SelfPreKeySignedDbo>(entity =>
        {
            entity.ToTable("SelfPreKeySigned");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedOnAdd();
            entity.Property(e => e.SelfIdentityId).IsRequired();
            entity.Property(e => e.SignedPreKeyId).IsRequired();
            entity.Property(e => e.SignedPreKeyPrivate).IsRequired();
            entity.Property(e => e.SignedPreKeyPublicSpki).IsRequired();
            entity.Property(e => e.PreKeySignature).IsRequired();
            entity.Property(e => e.ExpiresUtc).IsRequired();
            entity.HasIndex(e => new { e.SelfIdentityId, e.SignedPreKeyId }).IsUnique();
            entity.HasOne<SelfIdentityDbo>()
                .WithMany()
                .HasForeignKey(e => e.SelfIdentityId)
                .OnDelete(DeleteBehavior.Cascade)
                .IsRequired();
            entity.HasQueryFilter(e => _active != null && _active.Identity != null && e.SelfIdentityId == _active.Identity.SelfIdentityId.Value);
        });

        // SelfOneTimePreKey
        modelBuilder.Entity<SelfOneTimePreKeyDbo>(entity =>
        {
            entity.ToTable("SelfOneTimePreKeys");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedOnAdd();
            entity.Property(e => e.SelfIdentityId).IsRequired();
            entity.Property(e => e.OneTimePreKeyId).IsRequired();
            entity.Property(e => e.OneTimePreKeyPrivate).IsRequired();
            entity.Property(e => e.OneTimePreKeyPublicSpki).IsRequired();
            entity.Property(e => e.ReservedForRequestCorrelationId);
            entity.Property(e => e.ReservedUntilUtc);
            entity.HasIndex(e => new { e.SelfIdentityId, e.OneTimePreKeyId }).IsUnique();
            entity.HasIndex(e => new { e.SelfIdentityId, e.ReservedForRequestCorrelationId })
                .IsUnique()
                .HasFilter("\"ReservedForRequestCorrelationId\" IS NOT NULL");
            entity.HasOne<SelfIdentityDbo>()
                .WithMany()
                .HasForeignKey(e => e.SelfIdentityId)
                .OnDelete(DeleteBehavior.Cascade)
                .IsRequired();
            entity.HasQueryFilter(e => _active != null && _active.Identity != null && e.SelfIdentityId == _active.Identity.SelfIdentityId.Value);
        });

        // PreHandshakeSessions
        modelBuilder.Entity<PreHandshakeSessionDbo>(entity =>
        {
            entity.ToTable("PreHandshakeSessions");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedOnAdd();
            entity.Property(e => e.SelfIdentityId).IsRequired();
            entity.Property(e => e.LocalRequestId).IsRequired();
            entity.Property(e => e.RecipientPublicKeyHash);
            entity.Property(e => e.InitialRootKey).IsRequired();
            entity.Property(e => e.CreatedAtUtc).IsRequired();
            entity.Property(e => e.ExpiresAtUtc);
            entity.Property(e => e.RemoteIdentityKeySpki).IsRequired();
            entity.Property(e => e.RemoteIdentityKeySpkiHash);

            entity.HasIndex(e => e.SelfIdentityId);
            entity.HasIndex(e => new { e.SelfIdentityId, e.ExpiresAtUtc });
            entity.HasIndex(e => new { e.SelfIdentityId, e.RecipientPublicKeyHash });
            entity.HasIndex(e => new { e.SelfIdentityId, e.LocalRequestId }).IsUnique();
            entity.HasIndex(e => new { e.SelfIdentityId, e.RemoteIdentityKeySpkiHash, e.CreatedAtUtc });
            entity.HasQueryFilter(e => _active != null && _active.Identity != null && e.SelfIdentityId == _active.Identity.SelfIdentityId.Value);
        });

        // SelfIdentity
        modelBuilder.Entity<SelfIdentityDbo>(entity =>
        {
            entity.ToTable("SelfIdentity");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedOnAdd();
            entity.Property(e => e.PublicIdentityId).IsRequired();
            entity.Property(e => e.Name).IsRequired();
            entity.Property(e => e.LastUsedUtc)
                  .IsRequired()
                  .HasConversion(
                      v => v.ToUnixTimeMilliseconds(),
                      v => DateTimeOffset.FromUnixTimeMilliseconds(v));
            entity.HasIndex(e => e.Name).IsUnique();
            entity.HasIndex(e => e.PublicIdentityId); // non-unique
            entity.HasIndex(e => e.LastUsedUtc);
            entity.HasIndex(e => e.ActiveIdentityKeyFingerprint); // index for fast fingerprint lookup
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
            entity.HasQueryFilter(e => _active != null && _active.Identity != null && e.SelfIdentityId == _active.Identity.SelfIdentityId.Value);
        });

        // DirectSession
        modelBuilder.Entity<DirectSessionDbo>(entity =>
        {
            entity.ToTable("DirectSession");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id)
                .ValueGeneratedOnAdd();
            entity.Property(e => e.RemotePeerId)
                .IsRequired();
            entity.Property(e => e.SessionId)
                .IsRequired();
            entity.Property(e => e.SelfIdentityId)
                .IsRequired();
            // Non-unique helper indexes for common lookups
            entity.HasIndex(e => new { e.SelfIdentityId, e.RemotePeerId });
            entity.HasIndex(e => new { e.SelfIdentityId, e.SessionId });
            // TEMP: remove FK to PeerConnections during Network domain cutover; keep logical reference only
            entity.HasOne<SelfIdentityDbo>()
                .WithMany()
                .HasForeignKey(e => e.SelfIdentityId)
                .OnDelete(DeleteBehavior.Cascade)
                .IsRequired();
            entity.HasQueryFilter(e => _active != null && _active.Identity != null && e.SelfIdentityId == _active.Identity.SelfIdentityId.Value);
        });

        modelBuilder.Entity<PreKeyBundleDbo>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.PublicKey).IsRequired();
            entity.Property(e => e.PeerId)
                .IsRequired()
                .HasConversion(
                    v => v.Value,
                    v => new Percolator.Cryptography.Primitives.PeerId(v));

            // FK to authoritative peer identity catalog
            entity.HasOne<PeerIdentityDbo>()
                .WithMany()
                .HasForeignKey(d => d.PeerId)
                .OnDelete(DeleteBehavior.Cascade)
                .IsRequired();
        });

        modelBuilder.Entity<PeerPublicSigningKeyDbo>(entity =>
        {
            entity.ToTable("PeerPublicSigningKeys");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.PeerId).IsRequired();
            entity.Property(e => e.PublicKey).IsRequired();
            entity.Property(e => e.PublicKeyHash).IsRequired();
            entity.Property(e => e.ActiveAtUtc).IsRequired();
            // ExpiredAtUtc nullable

            entity.HasIndex(e => e.PeerId);
            entity.HasIndex(e => e.PublicKeyHash).IsUnique();
            entity.HasIndex(e => new { e.PeerId, e.ActiveAtUtc });
            entity.HasIndex(e => new { e.PeerId, e.ExpiredAtUtc });
        });

        // GroupCryptoStates (Signal Group V2 root key material)
        modelBuilder.Entity<GroupCryptoStateDbo>(entity =>
        {
            entity.ToTable("GroupCryptoStates");
            entity.HasKey(e => e.ConversationId);
            entity.Property(e => e.ConversationId).IsRequired();
            entity.Property(e => e.GroupMasterKeyBytes).IsRequired();
            entity.Property(e => e.CreatedAtUtc).IsRequired();
            entity.Property(e => e.UpdatedAtUtc).IsRequired();
            entity.HasOne<ConversationDbo>()
                .WithMany()
                .HasForeignKey(e => e.ConversationId)
                .OnDelete(DeleteBehavior.Cascade)
                .IsRequired();
        });

        // GroupMembers (group membership and roles)
        modelBuilder.Entity<Percolator.Infrastructure.Chat.Persistence.GroupMemberDbo>(entity =>
        {
            entity.ToTable("GroupMembers");
            entity.HasKey(e => new { e.ConversationId, e.PublicIdentityId });
            entity.Property(e => e.ConversationId).IsRequired();
            entity.Property(e => e.PublicIdentityId)
                .HasConversion(
                    v => v.Value,
                    v => new Percolator.Chat.GroupLedger.PublicIdentityId(v))
                .IsRequired();
            entity.Property(e => e.PeerId)
                .HasConversion(
                    v => v.Value,
                    v => new Percolator.Identity.PeerId(v))
                .IsRequired(false);
            entity.Property(e => e.SelfId).IsRequired(false);
            entity.Property(e => e.Role).IsRequired();
            entity.Property(e => e.JoinedAtUtc).IsRequired();
            entity.Property(e => e.RemovedAtUtc).IsRequired(false);
            entity.HasIndex(e => e.ConversationId);
            entity.HasOne<ConversationDbo>()
                .WithMany()
                .HasForeignKey(e => e.ConversationId)
                .OnDelete(DeleteBehavior.Cascade)
                .IsRequired();
        });

        // GroupStates (group metadata: epoch, name)
        modelBuilder.Entity<Percolator.Infrastructure.Chat.Persistence.GroupStateDbo>(entity =>
        {
            entity.ToTable("GroupStates");
            entity.HasKey(e => e.ConversationId);
            entity.Property(e => e.ConversationId).IsRequired();
            entity.Property(e => e.Epoch).IsRequired();
            entity.Property(e => e.Name).IsRequired(false);
            entity.Property(e => e.RelayPeerId)
                .HasConversion(
                    v => v.Value,
                    v => new Percolator.Identity.PeerId(v))
                .IsRequired();
            entity.Property(e => e.CreatedAtUtc).IsRequired();
            entity.Property(e => e.UpdatedAtUtc).IsRequired();
            entity.HasOne<ConversationDbo>()
                .WithMany()
                .HasForeignKey(e => e.ConversationId)
                .OnDelete(DeleteBehavior.Cascade)
                .IsRequired();
        });

        // PendingGroupInvitations (pending group invites received from remote peers)
        modelBuilder.Entity<Percolator.Infrastructure.Chat.Persistence.PendingGroupInvitationDbo>(entity =>
        {
            entity.ToTable("PendingGroupInvitations");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedOnAdd();
            entity.Property(e => e.ConversationId).IsRequired();
            entity.Property(e => e.InviterPeerId).IsRequired();
            entity.Property(e => e.CreatorIdentityKey).IsRequired();
            entity.Property(e => e.InitialMembersJson).IsRequired();
            entity.Property(e => e.GroupName).IsRequired(false);
            entity.Property(e => e.ReceivedAtUtc).IsRequired();
            entity.Property(e => e.Status).IsRequired();
            entity.HasIndex(e => e.ConversationId);
        });

        // SenderKeyRecords (Signal Protocol Sender Key state persistence)
        modelBuilder.Entity<Percolator.Infrastructure.Chat.Persistence.SenderKeyRecordDbo>(entity =>
        {
            entity.ToTable("SenderKeyRecords");
            entity.HasKey(e => new { e.ConversationId, e.SenderPeerId, e.DeviceId });
            entity.Property(e => e.ConversationId).IsRequired();
            entity.Property(e => e.SenderPeerId).IsRequired();
            entity.Property(e => e.DeviceId).IsRequired();
            entity.Property(e => e.RecordBytes).IsRequired();
        });

        modelBuilder.Entity<SignedPreKeyDbo>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.PublicKey).IsRequired();
            entity.Property(e => e.Signature).IsRequired();

            entity.HasOne(d => d.PreKeyBundle)
                .WithMany(p => p.SignedPreKeys)
                .HasForeignKey(d => d.PreKeyBundleId)
                .IsRequired();
        });

        modelBuilder.Entity<OneTimePreKeyDbo>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.PublicKey).IsRequired();

            entity.HasOne(d => d.PreKeyBundle)
                .WithMany(p => p.OneTimePreKeys)
                .HasForeignKey(d => d.PreKeyBundleId)
                .IsRequired();
        });

        modelBuilder.Entity<DhtNode>(builder =>
        {
            builder.HasKey(e => e.Id);
            builder.Property(e => e.Id)
                .HasConversion(new ValueConverter<NodeId, byte[]>(v => v.ToArray(), v => NodeId.FromBytes(v)));

            builder.Property(e => e.EndPoint)
                .HasConversion(new DnsEndPointValueConverter());
        });

        

        // DirectSessionConversation mapping
        modelBuilder.Entity<DirectSessionConversationDbo>(entity =>
        {
            entity.ToTable("DirectSessionConversations");
            entity.HasKey(e => new { e.SelfIdentityId, e.DirectSessionId });
            entity.Property(e => e.SelfIdentityId).IsRequired();
            entity.Property(e => e.DirectSessionId).IsRequired();
            entity.Property(e => e.ConversationId).IsRequired();
            entity.HasIndex(e => new { e.SelfIdentityId, e.ConversationId }).IsUnique();
            entity.HasQueryFilter(e => _active != null && _active.Identity != null && e.SelfIdentityId == _active.Identity.SelfIdentityId.Value);
        });

        // PendingSessions (Cryptography domain persistence)
        modelBuilder.Entity<PendingSessionDbo>(entity =>
        {
            entity.ToTable("PendingSessions");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.SelfIdentityId).IsRequired();
            entity.Property(e => e.RemotePeerId).IsRequired();
            entity.Property(e => e.ProtocolVersion).IsRequired();
            entity.Property(e => e.Invitation).IsRequired();
            entity.Property(e => e.RequestCorrelationId);
            entity.Property(e => e.IsRelayed).IsRequired();
            entity.Property(e => e.InviterIdentityKey);
            entity.Property(e => e.CallbackEndpointHost);
            entity.Property(e => e.CallbackEndpointPort);
            entity.Property(e => e.State).IsRequired();
            entity.Property(e => e.CreatedAtUtc).IsRequired();
            entity.HasIndex(e => new { e.SelfIdentityId, e.RemotePeerId });
            entity.HasQueryFilter(e => _active != null && _active.Identity != null && e.SelfIdentityId == _active.Identity.SelfIdentityId.Value);
        });

        // SentInvitations (Cryptography domain persistence)
        modelBuilder.Entity<SentInvitationDbo>(entity =>
        {
            entity.ToTable("SentInvitations");
            entity.HasKey(e => new { e.SelfIdentityId, e.RequestCorrelationId });
            entity.Property(e => e.SelfIdentityId).IsRequired();
            entity.Property(e => e.RequestCorrelationId).IsRequired();
            entity.Property(e => e.SignedPreKeyId).IsRequired();
            entity.Property(e => e.OneTimePreKeyId);
            entity.Property(e => e.TargetPeerId);
            entity.Property(e => e.TargetDisplayName);
            entity.Property(e => e.TargetEndpointHost);
            entity.Property(e => e.TargetEndpointPort);
            entity.Property(e => e.InviteRouteKind).IsRequired();
            entity.Property(e => e.InviteRelayHostPeerId);
            entity.Property(e => e.CreatedAtUtc).IsRequired();
            entity.Property(e => e.ExpiresAtUtc).IsRequired();
            entity.HasIndex(e => new { e.SelfIdentityId, e.ExpiresAtUtc });
            entity.HasOne<SelfIdentityDbo>()
                .WithMany()
                .HasForeignKey(e => e.SelfIdentityId)
                .OnDelete(DeleteBehavior.Cascade)
                .IsRequired();
            entity.HasQueryFilter(e => _active != null && _active.Identity != null && e.SelfIdentityId == _active.Identity.SelfIdentityId.Value);
        });

        // RatchetKeyIndex: fast lookup from (SelfIdentityId, RatchetPublicKey) -> DirectSessionId
        modelBuilder.Entity<RatchetKeyIndexDbo>(entity =>
        {
            entity.ToTable("RatchetKeyIndex");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.SelfIdentityId).IsRequired();
            entity.Property(e => e.DirectSessionId).IsRequired();
            entity.Property(e => e.RatchetPublicKey).IsRequired();
            entity.Property(e => e.UpdatedAtUtc).IsRequired();
            entity.HasIndex(e => new { e.SelfIdentityId, e.RatchetPublicKey }).IsUnique();

            // Global filter: only return rows for the active self identity (null active/identity matches nothing)
            entity.HasQueryFilter(e => _active != null && _active.Identity != null && e.SelfIdentityId == _active.Identity.SelfIdentityId.Value);
        });

        // Conversations
        modelBuilder.Entity<ConversationDbo>(entity =>
        {
            entity.ToTable("Conversations");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedNever();
            entity.Property(e => e.SelfIdentityId).IsRequired();
            entity.Property(e => e.Kind).IsRequired();
            entity.Property(e => e.CreatedAt).IsRequired();
            entity.Property(e => e.UpdatedAt).IsRequired();
            entity.HasIndex(e => e.Kind);
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
            entity.HasQueryFilter(e => _active != null && _active.Identity != null && e.SelfIdentityId == _active.Identity.SelfIdentityId.Value);
        });

        // Messages
        modelBuilder.Entity<MessageDbo>(entity =>
        {
            entity.ToTable("Messages");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedOnAdd();
            entity.Property(e => e.ConversationId).IsRequired();
            entity.Property(e => e.MessageGuid).IsRequired();
            entity.Property(e => e.SenderId).IsRequired();
            entity.Property(e => e.Body).IsRequired();
            entity.Property(e => e.SentAt).IsRequired();
            entity.HasIndex(e => new { e.ConversationId, e.SentAt });
            entity.HasIndex(e => new { e.ConversationId, e.MessageGuid }).IsUnique();
        });

        // ConversationParticipants (composite key)
        modelBuilder.Entity<ConversationParticipantDbo>(entity =>
        {
            entity.ToTable("ConversationParticipants");
            entity.HasKey(e => new { e.ConversationId, e.ParticipantId });
            entity.Property(e => e.ConversationId).IsRequired();
            entity.Property(e => e.ParticipantId).IsRequired();
        });

        // ReadReceipts
        modelBuilder.Entity<ReadReceiptDbo>(entity =>
        {
            entity.ToTable("ReadReceipts");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedOnAdd();
            entity.Property(e => e.ConversationId).IsRequired();
            entity.Property(e => e.MessageGuid).IsRequired();
            entity.Property(e => e.ReaderId).IsRequired();
            entity.Property(e => e.SentAt).IsRequired();
            entity.HasIndex(e => new { e.ConversationId, e.MessageGuid, e.ReaderId }).IsUnique();
            entity.HasOne(e => e.Conversation)
                .WithMany()
                .HasForeignKey(e => e.ConversationId)
                .OnDelete(DeleteBehavior.Cascade)
                .IsRequired();
        });

        // DeliveredReceipts
        modelBuilder.Entity<DeliveredReceiptDbo>(entity =>
        {
            entity.ToTable("DeliveredReceipts");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedOnAdd();
            entity.Property(e => e.ConversationId).IsRequired();
            entity.Property(e => e.MessageGuid).IsRequired();
            entity.Property(e => e.RecipientId).IsRequired();
            entity.Property(e => e.DeliveredAt).IsRequired();
            entity.HasIndex(e => new { e.ConversationId, e.MessageGuid, e.RecipientId }).IsUnique();
            entity.HasOne(e => e.Conversation)
                .WithMany()
                .HasForeignKey(e => e.ConversationId)
                .OnDelete(DeleteBehavior.Cascade)
                .IsRequired();
        });

        // EmojiReactions
        modelBuilder.Entity<EmojiReactionDbo>(entity =>
        {
            entity.ToTable("EmojiReactions");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedOnAdd();
            entity.Property(e => e.ConversationId).IsRequired();
            entity.Property(e => e.MessageGuid).IsRequired();
            entity.Property(e => e.ReactorId).IsRequired();
            entity.Property(e => e.Emoji).IsRequired();
            entity.Property(e => e.SentAt).IsRequired();
            entity.HasIndex(e => new { e.ConversationId, e.MessageGuid, e.ReactorId, e.Emoji }).IsUnique();
            entity.HasOne(e => e.Conversation)
                .WithMany()
                .HasForeignKey(e => e.ConversationId)
                .OnDelete(DeleteBehavior.Cascade)
                .IsRequired();
        });

        // MessageQueue
        modelBuilder.Entity<MessageQueueItemDbo>(entity =>
        {
            entity.ToTable("MessageQueue");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedOnAdd();
            entity.Property(e => e.AckId).IsRequired();
            entity.Property(e => e.RecipientPkh)
                .HasConversion(
                    v => v.ToArray(),
                    v => Percolator.Chat.Messaging.ValueObjects.Pkh.FromBytesOwned(v))
                .IsRequired();
            entity.Property(e => e.Blob).IsRequired();
            entity.Property(e => e.EnqueuedAtUtc).IsRequired();
            entity.HasIndex(e => e.EnqueuedAtUtc);
            entity.HasIndex(e => e.RecipientPkh);
            entity.HasIndex(e => new { e.RecipientPkh, e.EnqueuedAtUtc });
            entity.HasIndex(e => e.AckId).IsUnique();
        });

        // PeerRoutingProfile (new Network domain schema)
        modelBuilder.Entity<PeerRoutingProfileDbo>(entity =>
        {
            entity.ToTable("PeerRoutingProfiles");
            entity.HasKey(e => e.PeerId);
            entity.Property(e => e.PeerId).ValueGeneratedNever();
            entity.Property(e => e.DirectMessagePublicKey);
            entity.Property(e => e.ReachabilityStatus).IsRequired();
            entity.Property(e => e.ReachabilityLastChangeUtc);
        });

        modelBuilder.Entity<GrpcEndPointRoutingDbo>(entity =>
        {
            entity.ToTable("PeerRoutingGrpcEndPoints");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.PeerId).IsRequired();
            entity.Property(e => e.Host).IsRequired();
            entity.Property(e => e.Port).IsRequired();
            entity.Property(e => e.LastSeenUtc).IsRequired();
            entity.HasIndex(e => new { e.PeerId, e.Host, e.Port }).IsUnique();
            entity.HasOne<PeerRoutingProfileDbo>()
                .WithMany()
                .HasForeignKey(e => e.PeerId)
                .OnDelete(DeleteBehavior.Cascade)
                .IsRequired();
        });

        modelBuilder.Entity<RelayLinkDbo>(entity =>
        {
            entity.ToTable("PeerRoutingRelays");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.PeerId).IsRequired();
            entity.Property(e => e.RelayPeerId).IsRequired();
            entity.Property(e => e.LastSeenUtc).IsRequired();
            entity.HasIndex(e => new { e.PeerId, e.RelayPeerId }).IsUnique();
            entity.HasOne<PeerRoutingProfileDbo>()
                .WithMany()
                .HasForeignKey(e => e.PeerId)
                .OnDelete(DeleteBehavior.Cascade)
                .IsRequired();
        });

        modelBuilder.Entity<PeerRouteCandidateDbo>(entity =>
        {
            entity.ToTable("PeerRouteCandidates");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.SelfIdentityId).IsRequired();
            entity.Property(e => e.RemotePeerId).IsRequired();
            entity.Property(e => e.RouteKind).IsRequired();
            entity.Property(e => e.EndpointHost);
            entity.Property(e => e.EndpointPort);
            entity.Property(e => e.RelayHostPeerId);
            entity.Property(e => e.ObservedAtUtc).IsRequired();
            entity.Property(e => e.LastAttemptAtUtc);
            entity.Property(e => e.LastSuccessAtUtc);
            entity.Property(e => e.AttemptCount).IsRequired();
            entity.Property(e => e.LastError);
            entity.Property(e => e.Source).IsRequired();
            entity.HasIndex(e => new { e.SelfIdentityId, e.RemotePeerId, e.RouteKind, e.EndpointHost, e.EndpointPort, e.RelayHostPeerId }).IsUnique();
        });


        modelBuilder.Entity<DiscoveredPeerDbo>(entity =>
        {
            entity.ToTable("DiscoveredPeers");
            entity.HasKey(e => e.DiscoveryKey);
            entity.Property(e => e.DiscoveryKey).IsRequired();
            entity.Property(e => e.PublicKeyHash);
            entity.Property(e => e.FirstSeenUtc).IsRequired();
            entity.Property(e => e.LastSeenUtc).IsRequired();
            entity.Property(e => e.Source).IsRequired();
            entity.Property(e => e.Confidence).IsRequired();
            entity.Property(e => e.BoundPeerId);
            entity.HasIndex(e => e.PublicKeyHash);
        });

        modelBuilder.Entity<DiscoveredPeerEndpointDbo>(entity =>
        {
            entity.ToTable("DiscoveredPeerEndpoints");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.DiscoveryKey).IsRequired();
            entity.Property(e => e.Host).IsRequired();
            entity.Property(e => e.Port).IsRequired();
            entity.Property(e => e.FirstSeenUtc).IsRequired();
            entity.Property(e => e.LastSeenUtc).IsRequired();
            entity.HasIndex(e => new { e.DiscoveryKey, e.Host, e.Port }).IsUnique();
            entity.HasOne<DiscoveredPeerDbo>()
                .WithMany()
                .HasForeignKey(e => e.DiscoveryKey)
                .OnDelete(DeleteBehavior.Cascade)
                .IsRequired();
        });

        // RelayGroupStates (Relay Ledger state for group messaging)
        modelBuilder.Entity<RelayGroupStateDbo>(entity =>
        {
            entity.ToTable("RelayGroupStates");
            entity.HasKey(e => e.ConversationId);
            entity.Property(e => e.ConversationId).ValueGeneratedNever();
        });

        // RelayBlindedRosters (Blinded membership roster for relay fan-out)
        modelBuilder.Entity<RelayBlindedRosterDbo>(entity =>
        {
            entity.ToTable("RelayBlindedRosters");
            entity.HasKey(e => new { e.ConversationId, e.MemberPublicIdentityId });
            entity.Property(e => e.MemberPublicIdentityId)
                .HasConversion(
                    v => v.Value,
                    v => new Percolator.Identity.PublicIdentityId(v))
                .IsRequired();
        });

        // RelayOutbox (Outbox pattern for group provisioning events)
        modelBuilder.Entity<RelayOutboxDbo>(entity =>
        {
            entity.ToTable("RelayOutbox");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedOnAdd();
            entity.Property(e => e.EventType).IsRequired();
            entity.Property(e => e.PayloadJson).IsRequired();
            entity.Property(e => e.DestinationPeerId).IsRequired();
            entity.Property(e => e.ProcessedAtUtc);
            entity.HasIndex(e => e.ProcessedAtUtc);
        });
    }
}



