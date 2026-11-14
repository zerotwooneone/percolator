namespace Percolator.Infrastructure.Persistence.Entities;

using System;

/// <summary>
/// Per-identity launch settings stored inside the encrypted vault — replaces the plaintext
/// desktop sidecar store. Keyed by <see cref="Percolator.Domain.Identities.PublicIdentityId"/>
/// bytes so it covers personas and relay hosts alike.
/// </summary>
public sealed class IdentitySettingsEntity
{
    /// <summary>PK — <c>PublicIdentityId</c> as 16 raw bytes (BLOB), matching relay identity columns.</summary>
    public byte[] IdentityId { get; set; } = Array.Empty<byte>();
    public int PreferredPort { get; set; }
    public int? LastBoundPort { get; set; }
    public bool AutoStart { get; set; }
    public int SidebarOrder { get; set; }
}

/// <summary>Vault-global application setting (key/value).</summary>
public sealed class AppSettingEntity
{
    /// <summary>PK — namespaced key, e.g. <c>Gate.OnboardingCompleted</c>.</summary>
    public string SettingKey { get; set; } = string.Empty;
    public string SettingValue { get; set; } = string.Empty;
}
