namespace Percolator.Application.Ports;

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PublicIdentityId = Percolator.Domain.Identities.PublicIdentityId;

/// <summary>
/// Per-identity launch preferences persisted inside the encrypted vault. Keyed by
/// <see cref="PublicIdentityId"/> so personas and relay hosts share one table.
/// </summary>
public sealed record IdentityLaunchSettings(int PreferredPort, int? LastBoundPort, bool AutoStart, int SidebarOrder);

/// <summary>Vault-global app-setting keys — Application-owned constants; no magic strings in ViewModels.</summary>
public static class VaultSettingKeys
{
    /// <summary>The stored value convention for a set boolean flag.</summary>
    public const string FlagSetValue = "1";

    /// <summary>Set once the first successful persona launch completes; gates the `+` Exchange tile.</summary>
    public const string OnboardingCompleted = "Gate.OnboardingCompleted";

    /// <summary>The identity that was active when the last shell session ended.</summary>
    public const string LastActiveIdentityId = "Gate.LastActiveIdentityId";

    /// <summary>JSON-serialized <c>ShellWindowGeometry</c> — position, size, and window state (WIN-02).</summary>
    public const string ShellWindowGeometry = "Shell.Window.Geometry";
}

/// <summary>
/// Application port for vault-stored per-identity launch settings and global app settings.
/// Realized in the encrypted <c>percolator.db</c> — replaces the plaintext desktop sidecar
/// store. Implementations range-validate ports (1–65535) and throw
/// <see cref="System.ArgumentOutOfRangeException"/> for out-of-range values.
/// </summary>
public interface IVaultSettingsStore
{
    Task<IdentityLaunchSettings?> GetAsync(PublicIdentityId identityId, CancellationToken ct = default);
    Task<IReadOnlyList<(PublicIdentityId IdentityId, IdentityLaunchSettings Settings)>> GetAllAsync(CancellationToken ct = default);
    Task SaveAsync(PublicIdentityId identityId, IdentityLaunchSettings settings, CancellationToken ct = default);
    Task DeleteAsync(PublicIdentityId identityId, CancellationToken ct = default);
    Task<string?> GetAppSettingAsync(string key, CancellationToken ct = default);
    Task SetAppSettingAsync(string key, string value, CancellationToken ct = default);
}
