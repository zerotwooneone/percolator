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
    All = PreKeys | Profiles | Blobs | Groups
}

public sealed record RelayHostingPolicy
{
    public const int DefaultMaxOneTimePreKeys = 100;
    public const int DefaultMaxMailboxes = 1024;
    public const long DefaultMaxMailboxBytes = 64L * 1024 * 1024;
    public static readonly TimeSpan DefaultEnvelopeTtl = TimeSpan.FromDays(14);

    public RelayRegistrationStrategy RegistrationStrategy { get; init; }
    public bool AutoApproveLocalhost { get; init; }
    public RelayFeatureFlags EnabledFeatures { get; init; }
    public int MaxMailboxes { get; init; }
    public long MaxMailboxBytes { get; init; }
    public TimeSpan EnvelopeTtl { get; init; }
    public int MaxOneTimePreKeysPerIdentity { get; init; }

    public bool IsAcceptingPreKeys => IsFeatureEnabled(RelayFeatureFlags.PreKeys);

    public bool IsFeatureEnabled(RelayFeatureFlags flag) => (EnabledFeatures & flag) == flag;

    public PurgePolicy ToPurgePolicy() => new(
        DefaultTtl: EnvelopeTtl,
        MaxRetainedEnvelopes: MaxMailboxes * 100,
        MaxEnvelopeSizeBytes: PurgePolicy.DefaultMaxEnvelopeSizeBytes);

    public RelayHostingPolicy(
        RelayRegistrationStrategy registrationStrategy = RelayRegistrationStrategy.Open,
        bool autoApproveLocalhost = true,
        RelayFeatureFlags enabledFeatures = RelayFeatureFlags.All,
        int maxMailboxes = DefaultMaxMailboxes,
        long maxMailboxBytes = DefaultMaxMailboxBytes,
        TimeSpan? envelopeTtl = null,
        int maxOneTimePreKeysPerIdentity = DefaultMaxOneTimePreKeys)
    {
        if (maxMailboxes <= 0) throw new ArgumentOutOfRangeException(nameof(maxMailboxes));
        if (maxMailboxBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxMailboxBytes));
        var ttl = envelopeTtl ?? DefaultEnvelopeTtl;
        if (ttl <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(envelopeTtl));
        if (maxOneTimePreKeysPerIdentity <= 0) throw new ArgumentOutOfRangeException(nameof(maxOneTimePreKeysPerIdentity));

        RegistrationStrategy = registrationStrategy;
        AutoApproveLocalhost = autoApproveLocalhost;
        EnabledFeatures = enabledFeatures;
        MaxMailboxes = maxMailboxes;
        MaxMailboxBytes = maxMailboxBytes;
        EnvelopeTtl = ttl;
        MaxOneTimePreKeysPerIdentity = maxOneTimePreKeysPerIdentity;
    }

    public static RelayHostingPolicy Default { get; } = new();
    public static RelayHostingPolicy HostManaged { get; } = new(
        registrationStrategy: RelayRegistrationStrategy.ApprovalRequired,
        autoApproveLocalhost: true,
        enabledFeatures: RelayFeatureFlags.PreKeys | RelayFeatureFlags.Profiles | RelayFeatureFlags.Blobs | RelayFeatureFlags.Groups);
}
