using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Percolator.Desktop.Common.Storage;

/// <summary>
/// Read/write access to per-persona desktop launch settings (preferred port, last bound port,
/// auto-start flag, sidebar ordering) for the Identity Gate and unlock-time auto-start bypass.
/// </summary>
public interface IDesktopSettingsRepository
{
    Task<DesktopIdentitySettingsEntity?> GetAsync(Guid identityId, CancellationToken ct = default);

    Task<IReadOnlyList<DesktopIdentitySettingsEntity>> GetAllAsync(CancellationToken ct = default);

    Task SaveAsync(DesktopIdentitySettingsEntity entity, CancellationToken ct = default);

    Task DeleteAsync(Guid identityId, CancellationToken ct = default);

    /// <summary>Reads a vault-global UI flag (e.g. the Exchange onboarding gate); <c>null</c> when unset.</summary>
    Task<string?> GetAppSettingAsync(string key, CancellationToken ct = default);

    /// <summary>Persists a vault-global UI flag. Values are presentation state only — never secrets.</summary>
    Task SetAppSettingAsync(string key, string value, CancellationToken ct = default);
}
