namespace Percolator.Domain.Delivery.Hosting;

using System;
using Percolator.Domain.Delivery.ValueObjects;

public enum RelayRegistrationStrategy
{
    ApprovalRequired = 0, // Stage incoming mailbox registration requests for Exchange operator Approve/Reject
    Open = 1,             // Auto-approve incoming mailbox registrations up to MaxMailboxes
    Closed = 2            // Silently hang up on all new registration requests; existing mailboxes remain active
}

[Flags]
public enum RelayFeatureFlags
{
    None = 0,
    PreKeys = 1 << 0,
    Profiles = 1 << 1,
    Blobs = 1 << 2,
    Groups = 1 << 3,
    SwarmTracker = 1 << 4,
    TransferPipe = 1 << 5,
    DefaultCore = PreKeys | Profiles | Blobs | Groups,
    All = PreKeys | Profiles | Blobs | Groups | SwarmTracker | TransferPipe
}

public sealed record RelayHostingPolicy
{
    public const int DefaultMaxOneTimePreKeys = 100;
    public const int DefaultMaxMailboxes = 1024;
    public const long DefaultMaxMailboxBytes = 64L * 1024 * 1024;
    public static readonly TimeSpan DefaultEnvelopeTtl = TimeSpan.FromDays(14);

    public const int DefaultMaxActiveSwarms = 256;
    public const int DefaultMaxPeersPerSwarm = 64;
    public static readonly TimeSpan DefaultSwarmPeerEntryTtl = TimeSpan.FromMinutes(5);

    public const int DefaultMaxConcurrentPipes = 16;
    public const long DefaultMaxBytesPerSecondPerPipe = 10L * 1024 * 1024;
    public const long DefaultMaxBytesPerSession = 2L * 1024 * 1024 * 1024;
    public static readonly TimeSpan DefaultPipeIdleTimeout = TimeSpan.FromSeconds(60);

    public RelayRegistrationStrategy RegistrationStrategy { get; init; }
    public bool AutoApproveLocalhost { get; init; }
    public RelayFeatureFlags EnabledFeatures { get; init; }
    public int MaxMailboxes { get; init; }
    public long MaxMailboxBytes { get; init; }
    public TimeSpan EnvelopeTtl { get; init; }
    public int MaxOneTimePreKeysPerIdentity { get; init; }
    public int MaxActiveSwarms { get; init; }
    public int MaxPeersPerSwarm { get; init; }
    public TimeSpan SwarmPeerEntryTtl { get; init; }
    public int MaxConcurrentPipes { get; init; }
    public long MaxBytesPerSecondPerPipe { get; init; }
    public long MaxBytesPerSession { get; init; }
    public TimeSpan PipeIdleTimeout { get; init; }

    public bool IsAcceptingPreKeys => IsFeatureEnabled(RelayFeatureFlags.PreKeys);

    public bool IsFeatureEnabled(RelayFeatureFlags flag) => (EnabledFeatures & flag) == flag;

    public PurgePolicy ToPurgePolicy() => new(
        DefaultTtl: EnvelopeTtl,
        MaxRetainedEnvelopes: MaxMailboxes * 100,
        MaxEnvelopeSizeBytes: PurgePolicy.DefaultMaxEnvelopeSizeBytes);

    public RelayHostingPolicy(
        RelayRegistrationStrategy registrationStrategy = RelayRegistrationStrategy.Open,
        bool autoApproveLocalhost = true,
        RelayFeatureFlags enabledFeatures = RelayFeatureFlags.DefaultCore,
        int maxMailboxes = DefaultMaxMailboxes,
        long maxMailboxBytes = DefaultMaxMailboxBytes,
        TimeSpan? envelopeTtl = null,
        int maxOneTimePreKeysPerIdentity = DefaultMaxOneTimePreKeys,
        int maxActiveSwarms = DefaultMaxActiveSwarms,
        int maxPeersPerSwarm = DefaultMaxPeersPerSwarm,
        TimeSpan? swarmPeerEntryTtl = null,
        int maxConcurrentPipes = DefaultMaxConcurrentPipes,
        long maxBytesPerSecondPerPipe = DefaultMaxBytesPerSecondPerPipe,
        long maxBytesPerSession = DefaultMaxBytesPerSession,
        TimeSpan? pipeIdleTimeout = null)
    {
        if (maxMailboxes <= 0) throw new ArgumentOutOfRangeException(nameof(maxMailboxes));
        if (maxMailboxBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxMailboxBytes));
        var ttl = envelopeTtl ?? DefaultEnvelopeTtl;
        if (ttl <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(envelopeTtl));
        if (maxOneTimePreKeysPerIdentity <= 0) throw new ArgumentOutOfRangeException(nameof(maxOneTimePreKeysPerIdentity));
        if (maxActiveSwarms <= 0) throw new ArgumentOutOfRangeException(nameof(maxActiveSwarms));
        if (maxPeersPerSwarm <= 0) throw new ArgumentOutOfRangeException(nameof(maxPeersPerSwarm));
        var swarmTtl = swarmPeerEntryTtl ?? DefaultSwarmPeerEntryTtl;
        if (swarmTtl <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(swarmPeerEntryTtl));
        if (maxConcurrentPipes <= 0) throw new ArgumentOutOfRangeException(nameof(maxConcurrentPipes));
        if (maxBytesPerSecondPerPipe <= 0) throw new ArgumentOutOfRangeException(nameof(maxBytesPerSecondPerPipe));
        if (maxBytesPerSession <= 0) throw new ArgumentOutOfRangeException(nameof(maxBytesPerSession));
        var idleTimeout = pipeIdleTimeout ?? DefaultPipeIdleTimeout;
        if (idleTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(pipeIdleTimeout));

        RegistrationStrategy = registrationStrategy;
        AutoApproveLocalhost = autoApproveLocalhost;
        EnabledFeatures = enabledFeatures;
        MaxMailboxes = maxMailboxes;
        MaxMailboxBytes = maxMailboxBytes;
        EnvelopeTtl = ttl;
        MaxOneTimePreKeysPerIdentity = maxOneTimePreKeysPerIdentity;
        MaxActiveSwarms = maxActiveSwarms;
        MaxPeersPerSwarm = maxPeersPerSwarm;
        SwarmPeerEntryTtl = swarmTtl;
        MaxConcurrentPipes = maxConcurrentPipes;
        MaxBytesPerSecondPerPipe = maxBytesPerSecondPerPipe;
        MaxBytesPerSession = maxBytesPerSession;
        PipeIdleTimeout = idleTimeout;
    }

    public static RelayHostingPolicy Default { get; } = new();
    public static RelayHostingPolicy HostManaged { get; } = new(
        registrationStrategy: RelayRegistrationStrategy.ApprovalRequired,
        autoApproveLocalhost: true,
        enabledFeatures: RelayFeatureFlags.DefaultCore);
}
