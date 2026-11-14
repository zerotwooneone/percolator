// SPECIFICATION GOVERNANCE: Read Percolator.Desktop/spec/CodeBehind.spec.md before modifying this file.

using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Percolator.Application.Ports;

namespace Percolator.Desktop.Common.Windowing;

/// <summary>
/// <see cref="IShellWindowGeometryStore"/> over <see cref="IVaultSettingsStore"/> — a thin
/// adapter that serializes <see cref="ShellWindowGeometry"/> as JSON into the vault's
/// app_settings row. Corrupt or partial JSON is treated as absent (log once at Debug).
/// </summary>
internal sealed class VaultShellWindowGeometryStore : IShellWindowGeometryStore
{
    private static readonly JsonSerializerOptions JsonOptions = new();

    private readonly IVaultSettingsStore _store;
    private readonly ILogger<VaultShellWindowGeometryStore> _logger;

    public VaultShellWindowGeometryStore(
        IVaultSettingsStore store,
        ILogger<VaultShellWindowGeometryStore> logger)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<ShellWindowGeometry?> LoadAsync(CancellationToken ct = default)
    {
        var json = await _store
            .GetAppSettingAsync(VaultSettingKeys.ShellWindowGeometry, ct)
            .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            var geometry = JsonSerializer.Deserialize<ShellWindowGeometry>(json, JsonOptions);

            // A deserializable-but-empty record ("{}" or zeroed fields) is as useless as
            // garbage — first run falls back to the XAML CenterScreen default either way.
            return geometry is { Width: > 0, Height: > 0 } ? geometry : null;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            _logger.LogDebug(ex, "Stored shell window geometry is corrupt; treating as absent.");
            return null;
        }
    }

    public Task SaveAsync(ShellWindowGeometry geometry, CancellationToken ct = default) =>
        _store.SetAppSettingAsync(
            VaultSettingKeys.ShellWindowGeometry,
            JsonSerializer.Serialize(geometry, JsonOptions),
            ct);
}
