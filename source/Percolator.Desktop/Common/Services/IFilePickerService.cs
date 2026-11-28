// SPECIFICATION GOVERNANCE: Read Percolator.Desktop/spec/ViewModel.spec.md before modifying this file.

namespace Percolator.Desktop.Common.Services;

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Platform abstraction for opening file and directory pickers ([VM-014], [CB-007]).
/// </summary>
public interface IFilePickerService
{
    /// <summary>
    /// Displays a file picker dialog and returns the absolute paths of selected files.
    /// </summary>
    Task<IReadOnlyList<string>> PickFilesAsync(string title, bool allowMultiple = false, CancellationToken ct = default);

    /// <summary>
    /// Displays a folder picker dialog and returns the absolute path of the selected folder.
    /// </summary>
    Task<string?> PickFolderAsync(string title, CancellationToken ct = default);
}
