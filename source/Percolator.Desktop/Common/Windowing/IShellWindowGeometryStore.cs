// SPECIFICATION GOVERNANCE: Read Percolator.Desktop/spec/CodeBehind.spec.md before modifying this file.

using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;

namespace Percolator.Desktop.Common.Windowing;

/// <summary>
/// Last-known shell geometry in DIPs. <see cref="State"/> is recorded separately because
/// <c>Bounds</c> reflects the maximized rect while <see cref="WindowState.Maximized"/> — the
/// X/Y/Width/Height members always carry the Normal (restore) rect.
/// </summary>
public sealed record ShellWindowGeometry(
    double X,
    double Y,
    double Width,
    double Height,
    WindowState State);

/// <summary>
/// Presentation-state facade over the vault's app-settings ([WIN-02]): the shell's last
/// size/position/maximized state persists in the encrypted <c>percolator.db</c> under
/// <c>VaultSettingKeys.ShellWindowGeometry</c> — never a plaintext sidecar. Session-bound
/// singleton; dies with the session container.
/// </summary>
public interface IShellWindowGeometryStore
{
    /// <summary>Reads the saved geometry, or <c>null</c> on first run / corrupt record.</summary>
    Task<ShellWindowGeometry?> LoadAsync(CancellationToken ct = default);

    /// <summary>Writes the current geometry (JSON serialization owned by the store).</summary>
    Task SaveAsync(ShellWindowGeometry geometry, CancellationToken ct = default);
}
