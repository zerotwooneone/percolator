using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Percolator.Application.Ports;
using PublicIdentityId = Percolator.Domain.Identities.PublicIdentityId;

namespace Percolator.Desktop.Common.Storage;

/// <summary>
/// <see cref="IDesktopSettingsRepository"/> over the session-bound
/// <see cref="IVaultSettingsStore"/> — launch preferences and UI flags live inside the
/// encrypted vault, keyed by <see cref="PublicIdentityId"/> so personas and relay hosts
/// share one table (STOR-03). Resolved from the authenticated session container; the decoy
/// vault is a real session, so decoy rows persist identically and die with it.
/// </summary>
internal sealed class VaultDesktopSettingsRepository : IDesktopSettingsRepository
{
    private readonly IVaultSettingsStore _store;

    public VaultDesktopSettingsRepository(IVaultSettingsStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public Task<IdentityLaunchSettings?> GetAsync(Guid identityId, CancellationToken ct = default) =>
        _store.GetAsync(new PublicIdentityId(identityId), ct);

    public Task<IReadOnlyList<(PublicIdentityId IdentityId, IdentityLaunchSettings Settings)>> GetAllAsync(
        CancellationToken ct = default) =>
        _store.GetAllAsync(ct);

    public Task SaveAsync(Guid identityId, IdentityLaunchSettings settings, CancellationToken ct = default) =>
        _store.SaveAsync(new PublicIdentityId(identityId), settings, ct);

    public Task DeleteAsync(Guid identityId, CancellationToken ct = default) =>
        _store.DeleteAsync(new PublicIdentityId(identityId), ct);

    public Task<string?> GetAppSettingAsync(string key, CancellationToken ct = default) =>
        _store.GetAppSettingAsync(key, ct);

    public Task SetAppSettingAsync(string key, string value, CancellationToken ct = default) =>
        _store.SetAppSettingAsync(key, value, ct);
}
