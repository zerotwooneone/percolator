namespace Percolator.Infrastructure.Persistence;

using System;
using System.Linq;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Percolator.Domain.Security.Ports;
using Percolator.Infrastructure.Persistence.Converters;
using Percolator.Infrastructure.Persistence.Entities;

public class PercolatorDbContext : DbContext
{
    private readonly IMasterKeyHandle? _masterKeyHandle;
    private readonly AesGcmValueConverter? _valueConverter;
    private readonly NullableAesGcmValueConverter? _nullableValueConverter;

    public IMasterKeyHandle? MasterKeyHandle => _masterKeyHandle;

    public PercolatorDbContext(DbContextOptions<PercolatorDbContext> options, IMasterKeyHandle? masterKeyHandle = null)
        : base(options)
    {
        _masterKeyHandle = masterKeyHandle;
        if (_masterKeyHandle != null)
        {
            _valueConverter = new AesGcmValueConverter(_masterKeyHandle);
            _nullableValueConverter = new NullableAesGcmValueConverter(_masterKeyHandle);
        }
    }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        base.OnConfiguring(optionsBuilder);
        optionsBuilder.ReplaceService<IModelCacheKeyFactory, PercolatorModelCacheKeyFactory>();
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
            entity.Property(e => e.PrivateKeyCiphertext).HasColumnType("BLOB");
            if (_valueConverter != null)
            {
                entity.Property(e => e.PrivateKeyCiphertext).HasConversion(_valueConverter);
            }
        });

        // 4. Vault Signed Prekeys
        modelBuilder.Entity<VaultSignedPreKeyEntity>(entity =>
        {
            entity.ToTable("vault_signed_prekeys");
            entity.HasKey(e => new { e.OwnerIdentityId, e.DeviceId });
            entity.Property(e => e.PublicKey).HasColumnType("BLOB");
            entity.Property(e => e.Signature).HasColumnType("BLOB");
            entity.Property(e => e.PrivateKeyCiphertext).HasColumnType("BLOB");
            if (_valueConverter != null)
            {
                entity.Property(e => e.PrivateKeyCiphertext).HasConversion(_valueConverter);
            }
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
            entity.Property(e => e.PrivateKeyCiphertext).HasColumnType("BLOB");
            if (_valueConverter != null)
            {
                entity.Property(e => e.PrivateKeyCiphertext).HasConversion(_valueConverter);
            }
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
            entity.Property(e => e.RootKeyCiphertext).HasColumnType("BLOB");
            entity.Property(e => e.SendChainKeyCiphertext).HasColumnType("BLOB");
            entity.Property(e => e.RecvChainKeyCiphertext).HasColumnType("BLOB");
            entity.Property(e => e.LocalEphemeralPrivCiphertext).HasColumnType("BLOB");
            entity.Property(e => e.RatchetStateBlob).HasColumnType("BLOB");

            if (_valueConverter != null)
            {
                entity.Property(e => e.RootKeyCiphertext).HasConversion(_valueConverter);
            }
            if (_nullableValueConverter != null)
            {
                entity.Property(e => e.SendChainKeyCiphertext).HasConversion(_nullableValueConverter);
                entity.Property(e => e.RecvChainKeyCiphertext).HasConversion(_nullableValueConverter);
                entity.Property(e => e.LocalEphemeralPrivCiphertext).HasConversion(_nullableValueConverter);
                entity.Property(e => e.RatchetStateBlob).HasConversion(_nullableValueConverter);
            }

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
            entity.Property(e => e.ChainKeyCiphertext).HasColumnType("BLOB");
            entity.Property(e => e.SigningPrivCiphertext).HasColumnType("BLOB");

            if (_valueConverter != null)
            {
                entity.Property(e => e.ChainKeyCiphertext).HasConversion(_valueConverter);
            }
            if (_nullableValueConverter != null)
            {
                entity.Property(e => e.SigningPrivCiphertext).HasConversion(_nullableValueConverter);
            }
        });

        // 8. Vault Group Receiver Sessions
        modelBuilder.Entity<VaultGroupReceiverSessionEntity>(entity =>
        {
            entity.ToTable("vault_group_receiver_sessions");
            entity.HasKey(e => new { e.ChannelId, e.AuthorIdentityId, e.AuthorDeviceId });
            entity.Property(e => e.AuthorSigningPub).HasColumnType("BLOB");
            entity.Property(e => e.ChainKeyCiphertext).HasColumnType("BLOB");
            entity.Property(e => e.SkippedKeysBlob).HasColumnType("BLOB");

            if (_valueConverter != null)
            {
                entity.Property(e => e.ChainKeyCiphertext).HasConversion(_valueConverter);
            }
            if (_nullableValueConverter != null)
            {
                entity.Property(e => e.SkippedKeysBlob).HasConversion(_nullableValueConverter);
            }
        });

        // 9. Vault Group Credentials
        modelBuilder.Entity<VaultGroupCredentialsEntity>(entity =>
        {
            entity.ToTable("vault_group_credentials");
            entity.HasKey(e => e.ChannelId);
            entity.Property(e => e.AuthCredentialMac).HasColumnType("BLOB");
            entity.Property(e => e.MasterKeyCiphertext).HasColumnType("BLOB");
            entity.Property(e => e.LocalBlobKeyCiphertext).HasColumnType("BLOB");

            if (_valueConverter != null)
            {
                entity.Property(e => e.MasterKeyCiphertext).HasConversion(_valueConverter);
            }
            if (_nullableValueConverter != null)
            {
                entity.Property(e => e.LocalBlobKeyCiphertext).HasConversion(_nullableValueConverter);
            }
        });

        // 10. Vault Pending Handshakes
        modelBuilder.Entity<VaultPendingHandshakeEntity>(entity =>
        {
            entity.ToTable("vault_pending_handshakes");
            entity.HasKey(e => new { e.RecipientIdentityId, e.SenderIdentityId });
            entity.Property(e => e.EnvelopePayload).HasColumnType("BLOB");

            if (_valueConverter != null)
            {
                entity.Property(e => e.EnvelopePayload).HasConversion(_valueConverter);
            }
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

public class PercolatorModelCacheKeyFactory : ModelCacheKeyFactory
{
    public PercolatorModelCacheKeyFactory(ModelCacheKeyFactoryDependencies dependencies)
        : base(dependencies)
    {
    }

    public override object Create(DbContext context, bool designTime)
    {
        return context is PercolatorDbContext pContext && pContext.MasterKeyHandle != null
            ? (context.GetType(), (object)pContext.MasterKeyHandle, designTime)
            : base.Create(context, designTime);
    }
}
