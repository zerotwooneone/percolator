// SPECIFICATION GOVERNANCE: Read Percolator.Desktop/spec/ViewModel.spec.md before modifying this file.

using System.Threading;
using System.Threading.Tasks;

namespace Percolator.Desktop.Common.Services;

/// <summary>Outcome of a modal dialog interaction.</summary>
public enum DialogResult
{
    /// <summary>Closed via the secondary/close action, escape, or dismissal.</summary>
    None = 0,

    /// <summary>The affirmative primary action was chosen.</summary>
    Primary = 1
}

/// <summary>
/// Framework-agnostic descriptor for a modal confirmation prompt. ViewModels author
/// these; the dialog service realization owns all control and windowing mechanics.
/// </summary>
public sealed record DialogRequest(
    string Title,
    string Message,
    string PrimaryButtonText,
    string CloseButtonText);

/// <summary>
/// The application's dialog service abstraction ([VM-014]). Injected into ViewModels
/// for all user confirmations and modal prompts so Views and code-behind never
/// construct dialogs directly.
/// </summary>
public interface IDialogService
{
    /// <summary>Displays the modal prompt and returns the user's action.</summary>
    Task<DialogResult> ShowAsync(DialogRequest request, CancellationToken ct = default);
}
