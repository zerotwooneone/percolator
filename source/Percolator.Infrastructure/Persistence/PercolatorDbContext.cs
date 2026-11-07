namespace Percolator.Infrastructure.Persistence;

using System;
using System.Linq;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Percolator.Infrastructure.Persistence.Entities;

public class PercolatorDbContext : DbContext
{
    public PercolatorDbContext(DbContextOptions<PercolatorDbContext> options)
        : base(options)
    {
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        base.ConfigureConventions(configurationBuilder);
        configurationBuilder.Properties<DateTimeOffset>().HaveConversion<DateTimeOffsetToTicksConverter>();
        configurationBuilder.Properties<DateTimeOffset?>().HaveConversion<NullableDateTimeOffsetToTicksConverter>();
    }

    public DbSet<IdentityProfileEntity> IdentityProfiles => Set<IdentityProfileEntity>();
    public DbSet<LocalDeviceEntity> LocalDevices => Set<LocalDeviceEntity>();
    public DbSet<VaultIdentityKeyEntity> VaultIdentityKeys => Set<VaultIdentityKeyEntity>();
    public DbSet<VaultSignedPreKeyEntity> VaultSignedPreKeys => Set<VaultSignedPreKeyEntity>();
    public DbSet<VaultOneTimePreKeyEntity> VaultOneTimePreKeys => Set<VaultOneTimePreKeyEntity>();
    public DbSet<VaultRatchetSessionEntity> VaultRatchetSessions => Set<VaultRatchetSessionEntity>();
    public DbSet<VaultGroupSenderKeyEntity> VaultGroupSenderKeys => Set<VaultGroupSenderKeyEntity>();
    public DbSet<VaultGroupReceiverSessionEntity> VaultGroupReceiverSessions => Set<VaultGroupReceiverSessionEntity>();
    public DbSet<VaultGroupCredentialsEntity> VaultGroupCredentials => Set<VaultGroupCredentialsEntity>();
    public DbSet<VaultPendingHandshakeEntity> VaultPendingHandshakes => Set<VaultPendingHandshakeEntity>();
    public DbSet<OpsOutboxJobEntity> OpsOutboxJobs => Set<OpsOutboxJobEntity>();
    public DbSet<OpsInboundQueueEntity> OpsInboundQueue => Set<OpsInboundQueueEntity>();
    public DbSet<OpsReservedPortEntity> OpsReservedPorts => Set<OpsReservedPortEntity>();
    public DbSet<PeerContactEntity> PeerContacts => Set<PeerContactEntity>();
    public DbSet<PeerContactDeviceEntity> PeerContactDevices => Set<PeerContactDeviceEntity>();
    public DbSet<ChatChannelEntity> ChatChannels => Set<ChatChannelEntity>();
    public DbSet<ChatChannelMemberEntity> ChatChannelMembers => Set<ChatChannelMemberEntity>();
    public DbSet<ChatChannelPayloadEntity> ChatChannelPayloads => Set<ChatChannelPayloadEntity>();
    public DbSet<RelayPreKeyEntity> RelayPreKeys => Set<RelayPreKeyEntity>();
    public DbSet<RelayProfileSlotEntity> RelayProfileSlots => Set<RelayProfileSlotEntity>();
    public DbSet<RelayLedgerEpochEntity> RelayLedgerEpochs => Set<RelayLedgerEpochEntity>();
    public DbSet<RelayLedgerRoutingTokenEntity> RelayLedgerRoutingTokens => Set<RelayLedgerRoutingTokenEntity>();
    public DbSet<PeerProfileEntity> PeerProfiles => Set<PeerProfileEntity>();
    public DbSet<PeerProfileSnapshotEntity> PeerProfileSnapshots => Set<PeerProfileSnapshotEntity>();
    public DbSet<PeerMailboxRoutingEntity> PeerMailboxRouting => Set<PeerMailboxRoutingEntity>();

    // Chunk 4.1 Chat Messaging Entities
    public DbSet<ChatMessageEntity> ChatMessages => Set<ChatMessageEntity>();
    public DbSet<ChatReactionEntity> ChatReactions => Set<ChatReactionEntity>();
    public DbSet<ChatMessageMentionEntity> ChatMessageMentions => Set<ChatMessageMentionEntity>();
    public DbSet<ChatAttachmentEntity> ChatAttachments => Set<ChatAttachmentEntity>();
    public DbSet<ChatGroupInvitationEntity> ChatGroupInvitations => Set<ChatGroupInvitationEntity>();
    public DbSet<ChatUnknownGroupMessageEntity> ChatUnknownGroupMessages => Set<ChatUnknownGroupMessageEntity>();

    // Chunk 4.2 Discovery & Rendezvous Entities
    public DbSet<RelayPeerDirectoryEntity> RelayPeerDirectory => Set<RelayPeerDirectoryEntity>();
    public DbSet<RelayRendezvousEntryEntity> RelayRendezvousEntries => Set<RelayRendezvousEntryEntity>();

    // Chunk 4.4 File Transfer Entities
    public DbSet<TransferSessionEntity> TransferSessions => Set<TransferSessionEntity>();
    public DbSet<ManifestFileEntity> ManifestFiles => Set<ManifestFileEntity>();
    public DbSet<TransferBitfieldEntity> TransferBitfields => Set<TransferBitfieldEntity>();
    public DbSet<SwarmEndpointEntity> SwarmEndpoints => Set<SwarmEndpointEntity>();
    public DbSet<FilterPolicyEntity> FilterPolicies => Set<FilterPolicyEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        var byteArrayComparer = new ValueComparer<byte[]>(
            (c1, c2) => c1 == null ? c2 == null : c2 != null && c1.SequenceEqual(c2),
            c => c.Aggregate(0, (a, v) => HashCode.Combine(a, v.GetHashCode())),
            c => c == null ? Array.Empty<byte>() : c.ToArray());

        // 1. Identity Profiles
        modelBuilder.Entity<IdentityProfileEntity>(entity =>
        {
            entity.ToTable("identity_profiles");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.DisplayName).HasMaxLength(64).IsRequired();
        });

        // 2. Local Devices
        modelBuilder.Entity<LocalDeviceEntity>(entity =>
        {
            entity.ToTable("local_devices");
            entity.HasKey(e => new { e.OwnerIdentityId, e.DeviceId });
            entity.Property(e => e.DevicePublicKey).HasColumnType("BLOB");
            entity.Property(e => e.LinkProofSignature).HasColumnType("BLOB");
            entity.HasOne<IdentityProfileEntity>()
                .WithMany()
                .HasForeignKey(e => e.OwnerIdentityId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // 3. Vault Identity Keys
        modelBuilder.Entity<VaultIdentityKeyEntity>(entity =>
        {
            entity.ToTable("vault_identity_keys");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.PublicKey).HasColumnType("BLOB");
            entity.Property(e => e.PrivateKey).HasColumnType("BLOB");
        });

        // 4. Vault Signed Prekeys
        modelBuilder.Entity<VaultSignedPreKeyEntity>(entity =>
        {
            entity.ToTable("vault_signed_prekeys");
            entity.HasKey(e => new { e.OwnerIdentityId, e.DeviceId });
            entity.Property(e => e.PublicKey).HasColumnType("BLOB");
            entity.Property(e => e.Signature).HasColumnType("BLOB");
            entity.Property(e => e.PrivateKey).HasColumnType("BLOB");
            entity.HasOne<VaultIdentityKeyEntity>()
                .WithMany()
                .HasForeignKey(e => e.OwnerIdentityId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // 5. Vault OneTime Prekeys
        modelBuilder.Entity<VaultOneTimePreKeyEntity>(entity =>
        {
            entity.ToTable("vault_onetime_prekeys");
            entity.HasKey(e => new { e.OwnerIdentityId, e.DeviceId, e.KeyId });
            entity.Property(e => e.PublicKey).HasColumnType("BLOB");
            entity.Property(e => e.PrivateKey).HasColumnType("BLOB");
            entity.HasIndex(e => new { e.OwnerIdentityId, e.DeviceId, e.IsConsumed })
                .HasDatabaseName("ix_vault_opk_unconsumed");
            entity.HasOne<VaultIdentityKeyEntity>()
                .WithMany()
                .HasForeignKey(e => e.OwnerIdentityId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // 6. Vault Ratchet Sessions
        modelBuilder.Entity<VaultRatchetSessionEntity>(entity =>
        {
            entity.ToTable("vault_ratchet_sessions");
            entity.HasKey(e => e.SessionId);
            entity.Property(e => e.LocalEphemeralPub).HasColumnType("BLOB");
            entity.Property(e => e.RemoteEphemeralPub).HasColumnType("BLOB");
            entity.Property(e => e.RootKey).HasColumnType("BLOB");
            entity.Property(e => e.SendChainKey).HasColumnType("BLOB");
            entity.Property(e => e.RecvChainKey).HasColumnType("BLOB");
            entity.Property(e => e.LocalEphemeralPriv).HasColumnType("BLOB");
            entity.Property(e => e.RatchetStateBlob).HasColumnType("BLOB");

            entity.HasIndex(e => new { e.OwnerIdentityId, e.OwnerDeviceId, e.RemotePeerId, e.RemoteDeviceId })
                .IsUnique()
                .HasDatabaseName("ix_vault_ratchet_route");
        });

        // 7. Vault Group Sender Keys
        modelBuilder.Entity<VaultGroupSenderKeyEntity>(entity =>
        {
            entity.ToTable("vault_group_sender_keys");
            entity.HasKey(e => new { e.ChannelId, e.AuthorIdentityId, e.AuthorDeviceId });
            entity.Property(e => e.AuthorSigningPub).HasColumnType("BLOB");
            entity.Property(e => e.ChainKey).HasColumnType("BLOB");
            entity.Property(e => e.SigningPriv).HasColumnType("BLOB");
        });

        // 8. Vault Group Receiver Sessions
        modelBuilder.Entity<VaultGroupReceiverSessionEntity>(entity =>
        {
            entity.ToTable("vault_group_receiver_sessions");
            entity.HasKey(e => new { e.ChannelId, e.AuthorIdentityId, e.AuthorDeviceId });
            entity.Property(e => e.AuthorSigningPub).HasColumnType("BLOB");
            entity.Property(e => e.ChainKey).HasColumnType("BLOB");
            entity.Property(e => e.SkippedKeysBlob).HasColumnType("BLOB");
        });

        // 9. Vault Group Credentials
        modelBuilder.Entity<VaultGroupCredentialsEntity>(entity =>
        {
            entity.ToTable("vault_group_credentials");
            entity.HasKey(e => e.ChannelId);
            entity.Property(e => e.AuthCredentialMac).HasColumnType("BLOB");
            entity.Property(e => e.MasterKey).HasColumnType("BLOB");
            entity.Property(e => e.LocalBlobKey).HasColumnType("BLOB");
        });

        // 10. Vault Pending Handshakes
        modelBuilder.Entity<VaultPendingHandshakeEntity>(entity =>
        {
            entity.ToTable("vault_pending_handshakes");
            entity.HasKey(e => new { e.RecipientIdentityId, e.SenderIdentityId });
            entity.Property(e => e.EnvelopePayload).HasColumnType("BLOB");
        });

        // 11. Ops Outbox Jobs
        modelBuilder.Entity<OpsOutboxJobEntity>(entity =>
        {
            entity.ToTable("ops_outbox_jobs");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Payload).HasColumnType("BLOB");

            entity.HasIndex(e => new { e.Status, e.NextAttemptAtUtc })
                .HasDatabaseName("ix_ops_outbox_due");
            entity.HasIndex(e => new { e.OwnerIdentityId, e.Status })
                .HasDatabaseName("ix_ops_outbox_owner");
            entity.HasIndex(e => new { e.Status, e.CreatedAtUtc })
                .HasDatabaseName("ix_ops_outbox_retention");
        });

        // 12. Ops Inbound Queue
        modelBuilder.Entity<OpsInboundQueueEntity>(entity =>
        {
            entity.ToTable("ops_inbound_queue");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.RawPayload).HasColumnType("BLOB");
            entity.HasIndex(e => e.ProcessedAtUtc)
                .HasDatabaseName("ix_ops_inbound_unprocessed");
        });

        // 13. Ops Reserved Ports
        modelBuilder.Entity<OpsReservedPortEntity>(entity =>
        {
            entity.ToTable("ops_reserved_ports");
            entity.HasKey(e => e.PortNumber);
            entity.HasIndex(e => e.LeaseExpiryUtc)
                .HasDatabaseName("ix_ops_port_lease");
        });

        // 14. Peer Contacts
        modelBuilder.Entity<PeerContactEntity>(entity =>
        {
            entity.ToTable("peer_contacts");
            entity.HasKey(e => new { e.OwnerIdentityId, e.RemotePeerId });
            entity.Property(e => e.Nickname).HasMaxLength(64).IsRequired();
            entity.Property(e => e.PrimaryPublicKey).HasColumnType("BLOB");
            entity.HasIndex(e => new { e.OwnerIdentityId, e.Nickname })
                .HasDatabaseName("ix_peer_contact_nickname");
        });

        // 15. Peer Contact Devices
        modelBuilder.Entity<PeerContactDeviceEntity>(entity =>
        {
            entity.ToTable("peer_contact_devices");
            entity.HasKey(e => new { e.OwnerIdentityId, e.RemotePeerId, e.DeviceId });
            entity.HasOne<PeerContactEntity>()
                .WithMany()
                .HasForeignKey(e => new { e.OwnerIdentityId, e.RemotePeerId })
                .OnDelete(DeleteBehavior.Cascade);
        });

        // 16. Chat Channels
        modelBuilder.Entity<ChatChannelEntity>(entity =>
        {
            entity.ToTable("chat_channels");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).HasMaxLength(128).IsRequired();
            entity.HasIndex(e => e.LastActivityUtc)
                .HasDatabaseName("ix_chat_channel_activity");
            entity.HasIndex(e => new { e.Type, e.OwnerIdentityId, e.RemotePeerId })
                .HasDatabaseName("ix_chat_channel_direct");
        });

        // 17. Chat Channel Members
        modelBuilder.Entity<ChatChannelMemberEntity>(entity =>
        {
            entity.ToTable("chat_channel_members");
            entity.HasKey(e => new { e.ChannelId, e.MemberIdentityId });
            entity.HasOne<ChatChannelEntity>()
                .WithMany()
                .HasForeignKey(e => e.ChannelId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(e => e.MemberIdentityId)
                .HasDatabaseName("ix_chat_member_identity");
        });

        // 18. Chat Channel Payloads
        modelBuilder.Entity<ChatChannelPayloadEntity>(entity =>
        {
            entity.ToTable("chat_channel_payloads");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.EncryptedPayload).HasColumnType("BLOB");
            entity.HasOne<ChatChannelEntity>()
                .WithMany()
                .HasForeignKey(e => e.ChannelId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(e => new { e.ChannelId, e.SentAtUtc })
                .HasDatabaseName("ix_chat_payload_chronological");
        });

        // 19. Relay Prekeys
        modelBuilder.Entity<RelayPreKeyEntity>(entity =>
        {
            entity.ToTable("relay_prekeys");
            entity.HasKey(e => new { e.OwnerIdentityId, e.DeviceId });
            entity.Property(e => e.IdentityKey).HasColumnType("BLOB");
            entity.Property(e => e.SignedPreKey).HasColumnType("BLOB");
            entity.Property(e => e.SignedPreKeySignature).HasColumnType("BLOB");
            entity.Property(e => e.OneTimePreKeysJson).IsRequired();
            entity.HasIndex(e => e.UpdatedAtUtc)
                .HasDatabaseName("ix_relay_prekeys_updated");
        });

        // 20. Relay Profile Slots
        modelBuilder.Entity<RelayProfileSlotEntity>(entity =>
        {
            entity.ToTable("relay_profile_slots");
            entity.HasKey(e => e.BlindedLocator);
            entity.Property(e => e.BlindedLocator).HasColumnType("BLOB");
            entity.Property(e => e.WriteTokenHash).HasColumnType("BLOB");
            entity.Property(e => e.Ciphertext).HasColumnType("BLOB");
            entity.Property(e => e.Nonce).HasColumnType("BLOB");
            entity.HasIndex(e => e.ExpiresAtUtc)
                .HasDatabaseName("ix_relay_profile_expiry");
        });

        // 21. Relay Ledger Epochs
        modelBuilder.Entity<RelayLedgerEpochEntity>(entity =>
        {
            entity.ToTable("relay_ledger_epochs");
            entity.HasKey(e => e.ChannelId);
            entity.Property(e => e.EncryptedRosterBlob).HasColumnType("BLOB");
            entity.Property(e => e.ZkPublicParams).HasColumnType("BLOB");
            entity.HasIndex(e => e.RelayIdentityId)
                .HasDatabaseName("ix_relay_ledger_relay");
        });

        // 22. Relay Ledger Routing Tokens
        modelBuilder.Entity<RelayLedgerRoutingTokenEntity>(entity =>
        {
            entity.ToTable("relay_ledger_routing_tokens");
            entity.HasKey(e => new { e.ChannelId, e.RoutingToken });
            entity.HasOne<RelayLedgerEpochEntity>()
                .WithMany()
                .HasForeignKey(e => e.ChannelId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // 23. Peer Profiles
        modelBuilder.Entity<PeerProfileEntity>(entity =>
        {
            entity.ToTable("peer_profiles");
            entity.HasKey(e => e.PeerId);
            entity.Property(e => e.DisplayName).HasMaxLength(64).IsRequired();
            entity.Property(e => e.StatusBio).HasMaxLength(512);
        });

        // 24. Peer Profile Snapshots
        modelBuilder.Entity<PeerProfileSnapshotEntity>(entity =>
        {
            entity.ToTable("peer_profile_snapshots");
            entity.HasKey(e => new { e.PeerId, e.SourceEndpoint });
            entity.Property(e => e.BlindedLocator).HasColumnType("BLOB");
            entity.Property(e => e.MetadataJson).IsRequired();
            entity.HasIndex(e => new { e.PeerId, e.LastObservedAtUtc })
                .HasDatabaseName("ix_peer_snapshot_recency");
        });

        // 25. Peer Mailbox Routing
        modelBuilder.Entity<PeerMailboxRoutingEntity>(entity =>
        {
            entity.ToTable("peer_mailbox_routing");
            entity.HasKey(e => new { e.PeerId, e.RelayEndpoint });
            entity.Property(e => e.DeliveryToken).HasColumnType("BLOB");
            entity.HasIndex(e => new { e.PeerId, e.LastObservedAtUtc })
                .HasDatabaseName("ix_peer_mailbox_observed");
        });

        // 26. Chat Messages
        modelBuilder.Entity<ChatMessageEntity>(entity =>
        {
            entity.ToTable("chat_messages");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.AuthorId).HasColumnType("BLOB");
            entity.Property(e => e.ReplyAuthorId).HasColumnType("BLOB");
            entity.Property(e => e.DeletedBy).HasColumnType("BLOB");
            entity.Property(e => e.LinkPreviewThumbnailSha256).HasColumnType("BLOB");
            entity.Property(e => e.LinkPreviewThumbnailKey).HasColumnType("BLOB");
            entity.Property(e => e.LinkPreviewThumbnailNonce).HasColumnType("BLOB");
            entity.Property(e => e.TimestampUnixMs).HasConversion<long>();
            entity.Property(e => e.ExpiresAtUnixMs).HasConversion<long?>();
            entity.Property(e => e.DeliveredAtUnixMs).HasConversion<long?>();
            entity.Property(e => e.ReadAtUnixMs).HasConversion<long?>();
            entity.Property(e => e.EditedAtUnixMs).HasConversion<long?>();
            entity.Property(e => e.DeletedAtUnixMs).HasConversion<long?>();
            entity.HasIndex(e => new { e.ChannelId, e.TimestampUnixMs })
                .HasDatabaseName("ix_chat_timeline");
            entity.HasIndex(e => e.ExpiresAtUnixMs)
                .HasDatabaseName("ix_chat_expiry");
        });

        // 27. Chat Message Mentions
        modelBuilder.Entity<ChatMessageMentionEntity>(entity =>
        {
            entity.ToTable("chat_message_mentions");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.IdentityId).HasColumnType("BLOB");
            entity.HasIndex(e => e.IdentityId).HasDatabaseName("ix_chat_mention_identity");
            entity.HasOne<ChatMessageEntity>()
                .WithMany()
                .HasForeignKey(e => e.MessageId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // 28. Chat Reactions
        modelBuilder.Entity<ChatReactionEntity>(entity =>
        {
            entity.ToTable("chat_reactions");
            entity.HasKey(e => new { e.MessageId, e.ReactorId, e.Emoji });
            entity.Property(e => e.ReactorId).HasColumnType("BLOB");
            entity.Property(e => e.Emoji).HasMaxLength(32).IsRequired();
            entity.HasOne<ChatMessageEntity>()
                .WithMany()
                .HasForeignKey(e => e.MessageId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // 29. Chat Attachments
        modelBuilder.Entity<ChatAttachmentEntity>(entity =>
        {
            entity.ToTable("chat_attachments");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.BlobSha256).HasColumnType("BLOB");
            entity.Property(e => e.BaseNonce).HasColumnType("BLOB");
            entity.Property(e => e.EncryptionKey).HasColumnType("BLOB");
            entity.Property(e => e.Waveform).HasColumnType("BLOB");
            entity.Property(e => e.InlineBytes).HasColumnType("BLOB");
            entity.HasOne<ChatMessageEntity>()
                .WithMany()
                .HasForeignKey(e => e.MessageId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // 30. Chat Group Invitations
        modelBuilder.Entity<ChatGroupInvitationEntity>(entity =>
        {
            entity.ToTable("chat_group_invitations");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.InviterId).HasColumnType("BLOB");
            entity.Property(e => e.GroupName).IsRequired();
        });

        // 31. Chat Unknown Group Messages
        modelBuilder.Entity<ChatUnknownGroupMessageEntity>(entity =>
        {
            entity.ToTable("chat_unknown_group_messages");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.AuthorId).HasColumnType("BLOB");
            entity.Property(e => e.EncryptedPayload).HasColumnType("BLOB");
            entity.Property(e => e.ReceivedAtUnixMs).HasConversion<long>();
            entity.Property(e => e.ExpiresAtUnixMs).HasConversion<long>();
            entity.HasIndex(e => e.ExpiresAtUnixMs)
                .HasDatabaseName("ix_unknown_group_expiry");
            entity.HasIndex(e => e.ReceivedAtUnixMs)
                .HasDatabaseName("ix_unknown_group_received");
        });

        // 32. Relay Peer Directory (Chunk 4.2)
        modelBuilder.Entity<RelayPeerDirectoryEntity>(entity =>
        {
            entity.ToTable("relay_peer_directory");
            entity.HasKey(e => e.PeerId);
            entity.Property(e => e.PeerId).HasColumnType("BLOB");
            entity.HasIndex(e => e.LastUpdatedAtUtc)
                .HasDatabaseName("ix_peer_directory_updated");
        });

        // 33. Relay Rendezvous Entries (Chunk 4.2)
        modelBuilder.Entity<RelayRendezvousEntryEntity>(entity =>
        {
            entity.ToTable("relay_rendezvous_entries");
            entity.HasKey(e => e.BlindedLocator);
            entity.Property(e => e.BlindedLocator).HasColumnType("BLOB");
            entity.Property(e => e.EncryptedRoute).HasColumnType("BLOB");
            entity.HasIndex(e => e.PublishedAtUtc)
                .HasDatabaseName("ix_rendezvous_published");
        });

        // 34. File Transfer Sessions (Chunk 4.4)
        modelBuilder.Entity<TransferSessionEntity>(entity =>
        {
            entity.ToTable("ft_transfer_sessions");
            entity.HasKey(e => e.TransferId);
            entity.Property(e => e.LocalIdentityId).HasColumnType("BLOB").Metadata.SetValueComparer(byteArrayComparer);
            entity.Property(e => e.InitiatorId).HasColumnType("BLOB").Metadata.SetValueComparer(byteArrayComparer);
            entity.Property(e => e.MerkleRoot).HasColumnType("BLOB").Metadata.SetValueComparer(byteArrayComparer);
            entity.Property(e => e.ConcatenatedLeaves).HasColumnType("BLOB").Metadata.SetValueComparer(byteArrayComparer);
            entity.Property(e => e.BaseNonce).HasColumnType("BLOB").Metadata.SetValueComparer(byteArrayComparer);
            entity.Property(e => e.TransferKey).HasColumnType("BLOB").Metadata.SetValueComparer(byteArrayComparer);
            entity.Property(e => e.StreamAuthToken).HasColumnType("BLOB").Metadata.SetValueComparer(byteArrayComparer);
            entity.Property(e => e.RelayPipeToken).HasColumnType("BLOB").Metadata.SetValueComparer(byteArrayComparer);
        });

        // 35. File Transfer Manifest Files (Chunk 4.4)
        modelBuilder.Entity<ManifestFileEntity>(entity =>
        {
            entity.ToTable("ft_manifest_files");
            entity.HasKey(e => new { e.TransferId, e.FileIndex });
            entity.HasOne<TransferSessionEntity>()
                .WithMany()
                .HasForeignKey(e => e.TransferId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // 36. File Transfer Bitfields (Chunk 4.4)
        modelBuilder.Entity<TransferBitfieldEntity>(entity =>
        {
            entity.ToTable("ft_transfer_bitfields");
            entity.HasKey(e => e.TransferId);
            entity.Property(e => e.BitfieldBytes).HasColumnType("BLOB").Metadata.SetValueComparer(byteArrayComparer);
            entity.HasOne<TransferSessionEntity>()
                .WithOne()
                .HasForeignKey<TransferBitfieldEntity>(e => e.TransferId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // 37. Swarm Endpoints (Chunk 4.4)
        modelBuilder.Entity<SwarmEndpointEntity>(entity =>
        {
            entity.ToTable("ft_swarm_endpoints");
            entity.HasKey(e => new { e.RendezvousId, e.PeerId });
            entity.Property(e => e.RendezvousId).HasColumnType("BLOB").Metadata.SetValueComparer(byteArrayComparer);
            entity.Property(e => e.PeerId).HasColumnType("BLOB").Metadata.SetValueComparer(byteArrayComparer);
            entity.HasIndex(e => e.LastSeenUtc).HasDatabaseName("ix_swarm_endpoints_expiry");
        });

        // 38. File Transfer Filter Policies (Chunk 4.4)
        modelBuilder.Entity<FilterPolicyEntity>(entity =>
        {
            entity.ToTable("ft_filter_policies");
            entity.HasKey(e => e.OwnerId);
            entity.Property(e => e.OwnerId).HasColumnType("BLOB").Metadata.SetValueComparer(byteArrayComparer);
        });

        // Apply snake_case naming convention to all columns
        foreach (var entity in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entity.GetProperties())
            {
                property.SetColumnName(ToSnakeCase(property.Name));
            }
        }
    }

    private static string ToSnakeCase(string input)
    {
        if (string.IsNullOrEmpty(input)) return input;
        var sb = new StringBuilder(input.Length + 4);
        for (int i = 0; i < input.Length; i++)
        {
            char c = input[i];
            if (char.IsUpper(c))
            {
                if (i > 0)
                {
                    sb.Append('_');
                }
                sb.Append(char.ToLowerInvariant(c));
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }
}

public sealed class DateTimeOffsetToTicksConverter : ValueConverter<DateTimeOffset, long>
{
    public DateTimeOffsetToTicksConverter()
        : base(
            d => d.UtcTicks,
            ticks => new DateTimeOffset(ticks, TimeSpan.Zero))
    {
    }
}

public sealed class NullableDateTimeOffsetToTicksConverter : ValueConverter<DateTimeOffset?, long?>
{
    public NullableDateTimeOffsetToTicksConverter()
        : base(
            d => d.HasValue ? d.Value.UtcTicks : null,
            ticks => ticks.HasValue ? new DateTimeOffset(ticks.Value, TimeSpan.Zero) : null)
    {
    }
}
