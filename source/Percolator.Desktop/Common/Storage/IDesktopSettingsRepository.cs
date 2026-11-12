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
}
