// SPECIFICATION GOVERNANCE: Read Percolator.Desktop/spec/ViewModel.spec.md before modifying this file.

using System.Threading;
using System.Threading.Tasks;

namespace Percolator.Desktop.Common.Services;

/// <summary>
/// Abstraction over OS shell launches (open data folder, view logs) so ViewModels
/// never invoke platform process APIs directly.
/// </summary>
public interface ILauncherService
{
    /// <summary>Opens a filesystem directory in the OS file manager. Returns false on failure.</summary>
    Task<bool> OpenDirectoryAsync(string path, CancellationToken ct = default);
}
